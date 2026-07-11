# Dash Connect ⚡

**Однооконное Windows-приложение, снимающее блокировки РФ (Discord, YouTube, Telegram, игры) через движок [Zapret](https://github.com/bol-van/zapret) — без VPN.**

Одна кнопка **Подключить** — и работают Discord, YouTube, Telegram (в т.ч. моды: AyuGram, Nekogram и др.), игровые серверы (Fortnite / Apex / Dead by Daylight / FACEIT) и всё остальное заблокированное. Приложение запускается от администратора, само подбирает рабочую стратегию DPI-десинхронизации и живёт в системном трее.

---

## Возможности

- **Одна кнопка.** Один переключатель **Подключить / Отключить** поднимает всё.
- **Автоподбор стратегии.** Перед запуском проверяет Discord/YouTube/Telegram и подбирает самый быстрый рабочий пресет (ранний выход — не перебирает все).
- **Telegram и все моды.** Десинхронизация MTProto по IP дата-центров Telegram — работает для любого клиента (оригинал, AyuGram, Nekogram, Telegram X…).
- **Игры без VPN.** Игровые серверы (порт 5222 + домены Epic/EA/Faceit/Behaviour) обходятся чистым `split` (без fake-пакетов, которые ломают игры). Сами игры идут напрямую — родной пинг.
- **Само находит заблокированное.** `--hostlist-auto`: winws сам определяет и обходит любой заблокированный домен (самообучение).
- **Шифрованный DNS (DoH).** При подключении система переключается на Cloudflare 1.1.1.1 с шифрованием (лечит DNS-подмену), при отключении возвращается **исходный** DNS. Защита от подмены на уровне провайдера.
- **Установщик MSI + авто-обновление.** Ставится как обычная программа; при старте проверяет GitHub Releases и предлагает обновиться в один клик.
- **Системный трей.** Сворачивание в трей, фоновая работа, без лишних консольных окон.

---

## Установка

1. Скачайте **`DashConnect-1.0.0.msi`** со страницы [Releases](https://github.com/dutow20162007-create/DashConnect/releases/latest).
2. Запустите — установщик поставит приложение и все ассеты Zapret в `Program Files\Dash Connect`, создаст ярлыки.
3. Запустите **Dash Connect от имени администратора** (нужно для WinDivert) → **Подключить**.

Обновления приходят автоматически: при запуске приложение проверяет наличие новой версии и предлагает установить.

---

## Требования

- Windows 10 / 11 (x64)
- **Права администратора** (через UAC-манифест)

Приложение **самодостаточно** — среда .NET и все ассеты Zapret (winws, WinDivert, списки, пресеты) уже внутри установщика. Ничего доустанавливать не нужно.

---

## Build & run

```powershell
# from the project root
.\build.ps1        # produces dist\DashConnect.exe (self-contained, single file)
.\run.ps1          # builds if needed, then launches elevated (UAC)
```

`build.ps1` auto-detects the user-local .NET 8 SDK at `%USERPROFILE%\.dotnet` or the one on `PATH`.

### Verify the engine (no admin, no network changes)

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" run --project src\DashConnect.SelfTest
```

`DashConnect.SelfTest` parses the real Zapret presets, exercises the proxy‑URL parser and the sing‑box config generator, and prints a PASS/FAIL report — safe to run from any shell.

---

## How it works

### Subsystem A — DPI bypass (Web & Discord)

1. `StrategyProvider` enumerates every `*.bat` preset in the Zapret folder and resolves it into the exact `winws.exe` argument vector, reproducing cmd.exe’s `%BIN%` / `%LISTS%` / `%GameFilter*%` substitutions and `^!` escaping.
2. `ConnectivityTester` probes Discord & YouTube with fresh TCP + TLS handshakes (and a Discord‑gateway WebSocket), classifying each as **Open / Throttled / Blocked / Unreachable**.
3. `StrategySelector` baselines the direct connection, then trials each candidate preset through `winws.exe`, scores the result (open services − latency), and keeps the winner — accepting early when a preset makes everything open. *Fast scan* trials a curated 5‑preset subset; *Deep scan* trials all of them.
4. `ZapretManager` launches the chosen strategy and supervises the process; on stop it kills `winws.exe` and the WinDivert service.

### Subsystem B — Game routing (Fortnite / DBD / FACEIT)

1. `SingboxDownloader` fetches the latest stable `sing-box` Windows build from GitHub into `%AppData%\DashConnect\singbox` (idempotent).
2. `SingboxConfigBuilder` writes a `config.json` with a **TUN** inbound, your proxy as the `game-proxy` outbound, a `direct` outbound, and route rules that match the games by **process name** and **domain suffix**. `route.final = direct` keeps all other traffic on the ISP.
3. `SingboxManager` runs `sing-box run -c config.json` and supervises it.

Game signatures live in `%AppData%\DashConnect\game-routes.json` — edit it to add processes/domains.

> **You must supply the proxy.** Dash Connect never ships or hardcodes a proxy server. Paste a `vless://` or `ss://` link in **Settings → Game proxy URL**. Without it, game routing is skipped with a clear message.

---

## Project layout

```
DashConnect.sln
src/
  DashConnect.Core/         # engine (net8.0) — no UI, no admin manifest, fully testable
    Zapret/            #   StrategyProvider (batch parser), ZapretManager, HostlistManager
    Diagnostics/       #   ConnectivityTester, StrategySelector
    Singbox/           #   SingboxDownloader, SingboxConfigBuilder, ProxyUrlParser, GameRoutes, SingboxManager
    Services/          #   AppOrchestrator (the Connect/Disconnect brain)
    Config/ Models/ Util/ Logging/
  DashConnect.App/          # WPF UI (net8.0-windows) — dark dashboard, custom toggles, tray
  DashConnect.SelfTest/     # headless verification harness (net8.0 console)
build.ps1  run.ps1
```

`DashConnect.Core` targets `net8.0` and carries no admin manifest, so its logic can be exercised head‑lessly. Only the WPF shell carries `requireAdministrator`.

---

## Data & logs

Everything the app writes lives in `%AppData%\DashConnect\`:

| File | Purpose |
|------|---------|
| `config.json` | user settings |
| `game-routes.json` | editable game routing signatures |
| `singbox\sing-box.exe` + `config.json` | downloaded binary + generated tunnel config |
| `logs\dashconnect.log` | rolling activity log |

Open it quickly from **Settings → App data ▸**.

---

## Troubleshooting

- **“Administrator required”** — the app must be elevated. Use `run.ps1` or right‑click → *Run as administrator*.
- **A strategy never makes Discord open** — enable **Deep scan** to trial every preset, or add the blocked domain under **Settings → Add blocked domain** (it appends to `list-general-user.txt`).
- **Game routing does nothing** — confirm the proxy URL is a valid `vless://`/`ss://` link and that sing‑box downloaded (watch the log). Check `%AppData%\DashConnect\singbox\config.json`.
- **`winws` won’t start** — a previous instance may hold WinDivert; Dash Connect reaps orphans automatically, but a reboot clears a wedged driver.

---

## Notes on intent

Dash Connect is a personal connectivity tool: it restores access to services that are throttled/geoblocked and keeps game traffic fast. It wraps two well‑known open‑source projects (Zapret, sing‑box) with a friendly GUI and automation. Use it in accordance with the terms of the services you connect to and your local law.
