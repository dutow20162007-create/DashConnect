# Rebuilding `tg-ws-proxy.exe`

The Telegram WebSocket bridge (`zapret\tg-ws-proxy.exe`) is a **headless** build of
[Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) — a local MTProto proxy that wraps
Telegram traffic in WebSocket/TLS to Telegram's own data-centres (looks like plain HTTPS to DPI, no
third-party server). Dash Connect runs it on `127.0.0.1:1443` and points Telegram at it via
`tg://proxy?server=127.0.0.1&port=1443&secret=dd<secret>`.

It is committed as a prebuilt binary so the MSI is standalone. To rebuild from source:

```powershell
# 1. Get the source (the console entry point proxy.tg_ws_proxy:main is headless — no tray)
#    e.g. C:\Users\HU9O\Desktop\Flowseal-tg-ws-proxy-4f9edf3
cd <flowseal-src>

# 2. Runtime deps (headless needs only stdlib + cryptography; the tray deps are NOT needed)
python -m pip install cryptography pyinstaller

# 3. Tiny entry wrapper so PyInstaller resolves the `proxy` package cleanly
#    run_headless.py:
#        from proxy.tg_ws_proxy import main
#        if __name__ == '__main__':
#            main()

# 4. Build the onefile console exe straight into the Zapret folder
python -m PyInstaller --onefile --console --clean --noconfirm --name tg-ws-proxy `
  --collect-submodules proxy `
  --distpath "C:\Users\HU9O\Desktop\zapret-discord-youtube\zapret-discord-youtube-1.9.9c" `
  run_headless.py
```

`publish.ps1` then mirrors the Zapret folder into `zapret\` (repo) and the MSI bundles it via
`<Files Include="..\zapret\**" />`.

## CLI (how Dash Connect launches it)

```
tg-ws-proxy.exe --port 1443 --secret <32-hex>
```

The secret is generated once by the app and persisted in `config.json` (`TgWsProxySecret`), so the
`tg://proxy` link Telegram remembers keeps matching across restarts. Other flags (`--fake-tls-domain`,
`--dc-ip`, `--no-cfproxy`, `--log-file`) are available but unused by default.
