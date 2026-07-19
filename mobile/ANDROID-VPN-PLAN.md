# Dash Connect Android — VLESS / Amnezia tunnel integration

**Status: DONE for VLESS + plain WireGuard.** The native tunnel engine is built and wired — no stubs.
sing-box `libbox` is compiled from source with gomobile (all 4 ABIs) and drives a real full-device
`VpnService`. AmneziaWG obfuscation (Jc/S1/H1…) still runs as plain WireGuard (mainline sing-box
limitation — see Track B). End-to-end still needs the user's real subscription/server + a device to
verify against their ISP.

## What was built

- **Toolchain:** Go 1.23.4 at `<WORKDIR>/go-sdk`, GOPATH `<WORKDIR>/go`, env in
  `<WORKDIR>/goenv.sh`. SagerNet gomobile fork `v0.1.4` (the upstream one does NOT work — the
  sing-box build tool requires the fork). NDK `27.1.12297006`.
- **Engine:** cloned sing-box `v1.11.15` to `<WORKDIR>/singbox-src`; built libbox with:
  ```
  gomobile bind -v -target android -androidapi 21 -javapkg=io.nekohasekai -libname=box \
    -trimpath -buildvcs=false -ldflags "-X .../constant.Version=v1.11.15 -s -w -buildid=" \
    -tags "with_gvisor,with_quic,with_wireguard,with_ech,with_utls,with_clash_api" \
    ./experimental/libbox
  ```
  Output `libbox.aar` (41 MB) → `app/libs/libbox.aar`, wired via `implementation(files("libs/libbox.aar"))`.
- **Integration (Kotlin):**
  - `services/SingBoxVpnService.kt` — `VpnService` that implements `io.nekohasekai.libbox.PlatformInterface`.
    `Libbox.setup()` once, `Libbox.newService(config, this)`, `svc.start()`. `openTun()` builds the
    VpnService TUN from libbox `TunOptions` (address/routes/MTU/DNS); `autoDetectInterfaceControl()`
    `protect()`s outbound sockets; a `ConnectivityManager` default-network monitor + `getInterfaces()`
    feed libbox the live network. gomobile all-caps getters (`getMTU`, `getDNSServer`,
    `getDNSServerAddress`) are called explicitly to dodge Kotlin property-name ambiguity.
  - `ui/TunnelConfig.kt` — `parseVless()` → sing-box vless outbound; `buildLibboxConfig()` (no fd,
    `auto_route=true`, `stack=gvisor`, the DNS-hijack block the PC app taught us); `parseAmneziaOutbound()`
    → sing-box `wireguard` outbound.
  - `services/EngineRegistry.kt` + `ServiceManager.startTunnel()` — DPI-bypass (ByeDPI) and the tunnel
    are mutually exclusive (Android = one VpnService); Stop routes to the running one.
  - Manifest: `SingBoxVpnService` registered with `BIND_VPN_SERVICE`.
  - UI: Settings → "Режим подключения" (DPI / VLESS / Amnezia) + paste fields, wired through
    `MainActivity` to prefs (`dash_conn_mode`, `dash_vless_url`, `dash_amnezia_conf`).

## Track B — AmneziaWG obfuscation (remaining)

Mainline sing-box `wireguard` ignores Jc/Jmin/Jmax/S1/S2/H1-H4/I1, so an obfuscated `.conf` currently
runs as plain WireGuard. To get true AmneziaWG on the phone, either build libbox from a sing-box fork
with AmneziaWG (e.g. a `with_amneziawg`-style fork) or add `amneziawg-android`'s GoBackend (amneziawg-go
via gomobile) as a second engine. Same gomobile toolchain as above.

## Telegram MTProto — DONE (no native lib)

`ui/TelegramMtProto.kt` + the "Настроить Telegram (MTProto)" button: fetches public MTProto proxy
lists, TCP-tests them on the phone, opens `tg://proxy?...` so Telegram enables it in one tap.
