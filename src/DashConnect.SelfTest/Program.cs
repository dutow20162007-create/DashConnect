using System.Text.Json;
using DashConnect.Core.Config;
using DashConnect.Core.Network;
using DashConnect.Core.Singbox;
using DashConnect.Core.Zapret;

// Headless verification harness. Exercises the pure engine logic (batch parsing, proxy URL parsing,
// sing-box config generation) against the REAL Zapret folder — no admin, no network, no UAC — so the
// core can be validated in CI or from a plain shell.

var root = args.Length > 0
    ? args[0]
    : @"C:\Users\HU9O\Desktop\zapret-discord-youtube\zapret-discord-youtube-1.9.9c";

int failures = 0;
void Check(bool ok, string label)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
    if (!ok) failures++;
}

Console.WriteLine("== DashConnect SelfTest ==");
Console.WriteLine($"Zapret root: {root}");
Console.WriteLine();

// ---- 1. Zapret root validity ----
Console.WriteLine("[1] Zapret root & strategy parsing");
Check(StrategyProvider.IsValidRoot(root), "root is valid (winws.exe + lists present)");

var strategies = StrategyProvider.LoadAll(root, GameFilterMode.Disabled);
Check(strategies.Count > 0, $"parsed {strategies.Count} preset(s)");

var general = strategies.FirstOrDefault(s => s.Name == "general");
Check(general is not null, "found 'general' preset");

if (general is not null)
{
    var args0 = general.Arguments;
    Check(args0.Count > 10, $"'general' resolved to {args0.Count} args");
    Check(args0.Any(a => a.StartsWith("--wf-tcp=")), "contains --wf-tcp");
    Check(args0.All(a => !a.Contains("winws.exe")), "exe prefix stripped");

    bool noUnresolvedVars = strategies.SelectMany(s => s.Arguments).All(a => !a.Contains('%'));
    Check(noUnresolvedVars, "no unresolved %VARS% remain in any strategy");

    bool noCarets = strategies.SelectMany(s => s.Arguments).All(a => !a.Contains('^'));
    Check(noCarets, "no leftover ^ escape characters");

    bool listsResolved = args0.Any(a => a.Contains("lists") && a.Contains(".txt"));
    Check(listsResolved, "hostlist paths resolved to lists .txt");

    bool binResolved = args0.Any(a => a.Contains(".bin"));
    Check(binResolved, "fake-payload .bin paths present");

    Console.WriteLine();
    Console.WriteLine("  Sample resolved args (first 6):");
    foreach (var a in args0.Take(6)) Console.WriteLine($"    {a}");
    Console.WriteLine("  Fast-scan subset that will be tested first:");
    foreach (var name in StrategyProvider.FastSubset)
        Console.WriteLine($"    - {name} {(strategies.Any(s => s.Name == name) ? "(present)" : "(missing)")}");
}
Console.WriteLine();

// ---- 2. GameFilter substitution ----
Console.WriteLine("[2] GameFilter substitution");
var gfAll = StrategyProvider.LoadAll(root, GameFilterMode.All).FirstOrDefault(s => s.Name == "general");
if (gfAll is not null)
{
    bool hasRange = gfAll.Arguments.Any(a => a.Contains("1024-65535"));
    Check(hasRange, "GameFilter=All expands to 1024-65535");
}
if (general is not null)
{
    bool hasDisabled = general.Arguments.Any(a => a.EndsWith("=12") || a.Contains(",12"));
    Check(hasDisabled, "GameFilter=Disabled expands to inert port 12");
}
Console.WriteLine();

// ---- 3. Proxy URL parsing (synthetic, no real secrets) ----
Console.WriteLine("[3] Proxy URL parsing");
var vless = "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&sni=example.com&type=ws&path=%2Fws&fp=chrome#demo";
var vOut = ProxyUrlParser.Parse(vless, out var vErr);
Check(vOut is not null, $"VLESS parsed ({vErr ?? "ok"})");
if (vOut is not null)
{
    Check((string?)vOut.GetValueOrDefault("type") == "vless", "type=vless");
    Check((string?)vOut.GetValueOrDefault("server") == "example.com", "server=example.com");
    Check(vOut.ContainsKey("tls"), "tls block present");
    Check(vOut.ContainsKey("transport"), "ws transport present");
}

var ss = "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@example.com:8388#demo"; // aes-256-gcm:password
var sOut = ProxyUrlParser.Parse(ss, out var sErr);
Check(sOut is not null, $"Shadowsocks parsed ({sErr ?? "ok"})");
if (sOut is not null)
{
    Check((string?)sOut.GetValueOrDefault("method") == "aes-256-gcm", "ss method decoded");
    Check((string?)sOut.GetValueOrDefault("password") == "password", "ss password decoded");
}

Check(ProxyUrlParser.Parse("http://nope", out _) is null, "rejects unsupported scheme");
Console.WriteLine();

// ---- 4. Sing-box config generation ----
Console.WriteLine("[4] Game routes");
var routes = GameRoutesStore.Defaults();
Check(routes.Games.Count >= 3, $"default routes cover {routes.Games.Count} games");
Check(routes.AllProcessNames.Any(p => p.Contains("Fortnite")), "Fortnite process rule present");
Console.WriteLine();

// ---- 5. Subscription: VMess / Trojan parsing + tunnel builder ----
Console.WriteLine("[5] Subscription profiles & tunnel builder");

var vmessJson = "{\"v\":\"2\",\"ps\":\"Test VM\",\"add\":\"example.com\",\"port\":\"443\"," +
                "\"id\":\"11111111-2222-3333-4444-555555555555\",\"aid\":\"0\",\"net\":\"ws\"," +
                "\"host\":\"example.com\",\"path\":\"/vm\",\"tls\":\"tls\",\"sni\":\"example.com\"}";
var vmessLink = "vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vmessJson));
var trojanLink = "trojan://pass123@example.com:443?security=tls&sni=example.com&type=ws&path=%2Ftj#Test%20TJ";

var pVmess = SubscriptionManager.ParseProfile(vmessLink);
var pTrojan = SubscriptionManager.ParseProfile(trojanLink);
var pVless = SubscriptionManager.ParseProfile(vless);
Check(pVmess?.Protocol == "vmess" && pVmess.Name == "Test VM", "vmess profile parsed (name from ps)");
Check(pTrojan?.Protocol == "trojan" && pTrojan.Name == "Test TJ", "trojan profile parsed (name from #)");
Check(pVless?.Protocol == "vless", "vless profile parsed");

foreach (var (p, label) in new[] { (pVmess, "vmess"), (pTrojan, "trojan"), (pVless, "vless") })
{
    if (p is null) { Check(false, $"{label} outbound"); continue; }
    var ob = SubscriptionManager.BuildOutbound(p, out var obErr);
    Check(ob is not null && (string?)ob.GetValueOrDefault("type") == label, $"{label} outbound built ({obErr ?? "ok"})");
}

if (pVless is not null)
{
    var full = SingboxTunnelBuilder.BuildAndSave(pVless, full: true, routes);
    var smart = SingboxTunnelBuilder.BuildAndSave(pVless, full: false, routes);
    Check(full.Ok && full.Json is not null, "tunnel config (ТУННЕЛЬ/full) built");
    Check(smart.Ok && smart.Json is not null, "tunnel config (ПРОКСИ/smart) built");
    if (full.Json is not null)
    {
        using var doc = JsonDocument.Parse(full.Json);
        var final = doc.RootElement.GetProperty("route").GetProperty("final").GetString();
        Check(final == "game-proxy", "ТУННЕЛЬ: route.final = proxy (everything tunneled)");
    }
    if (smart.Json is not null)
    {
        using var doc = JsonDocument.Parse(smart.Json);
        var final = doc.RootElement.GetProperty("route").GetProperty("final").GetString();
        Check(final == "direct", "ПРОКСИ: route.final = direct (split tunnel, low ping)");
    }
}

// Regression guard: the VPN server's own address MUST be routed direct. Without this rule sing-box
// dials the server, auto_route captures that dial in our own TUN, route.final feeds it back into the
// same proxy outbound, and the tunnel reports "up" while carrying nothing at all. Uses a bare IP
// (TEST-NET-3) so the assertion needs no DNS and can't flake offline.
var vlessIp = "vless://11111111-2222-3333-4444-555555555555@203.0.113.7:443?security=none&type=tcp#ip";
var pIp = SubscriptionManager.ParseProfile(vlessIp);
if (pIp is not null)
{
    var built = SingboxTunnelBuilder.BuildAndSave(pIp, full: true, routes);
    var pinned = false;
    if (built.Json is not null)
    {
        using var doc = JsonDocument.Parse(built.Json);
        foreach (var rule in doc.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray())
        {
            if (rule.TryGetProperty("ip_cidr", out var cidrs) &&
                rule.TryGetProperty("outbound", out var ob) && ob.GetString() == "direct" &&
                cidrs.EnumerateArray().Any(c => c.GetString() == "203.0.113.7/32"))
                pinned = true;
        }
    }
    Check(pinned, "сервер вне туннеля: адрес VPN-сервера идёт direct (нет петли маршрутизации)");
}
Console.WriteLine();

// ---- 6. AmneziaWG (.conf) parser ----
Console.WriteLine("[6] AmneziaWG config parser");
const string awgConf = """
[Interface]
PrivateKey = 4PbjXzv5iMj9r6hKTczUw5CiVhiqKb8pZMT+w6R/okI=
Address = 10.1.5.201/32
Jc = 9
S1 = 110
H1 = 7291435-486117520
I1 = <b 0x0003><r 2>

[Peer]
PublicKey = FRJV+oOoYMa0qi0a317UtdLzLkYz8t6GV/pFAsFJ5mc=
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = list-crept-city-3a87a8.relay7ai.net:50001
""";
var awg = AmneziaWgManager.Parse(awgConf);
Check(awg.Valid, "valid AmneziaWG .conf accepted");
Check(awg.Endpoint == "list-crept-city-3a87a8.relay7ai.net:50001", "endpoint extracted");
Check(awg.Obfuscated, "AmneziaWG obfuscation params detected (Jc/S1/H1)");

const string plainWg = "[Interface]\nPrivateKey = k\n\n[Peer]\nEndpoint = 1.2.3.4:51820\nAllowedIPs = 0.0.0.0/0";
var wg = AmneziaWgManager.Parse(plainWg);
Check(wg.Valid && !wg.Obfuscated, "plain WireGuard parsed, not flagged obfuscated");

Check(!AmneziaWgManager.Parse("").Valid, "empty config rejected");
Check(!AmneziaWgManager.Parse("[Interface]\nPrivateKey = k").Valid, "config without [Peer]/Endpoint rejected");
Console.WriteLine();

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
