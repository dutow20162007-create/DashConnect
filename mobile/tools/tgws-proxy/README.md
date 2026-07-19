# tgws-proxy — исходник Telegram-моста

Это исходный код бинарников `app/src/main/jniLibs-extra/<abi>/libtgwsproxy.so`, которые
приложение запускает как локальный MTProto-мост: Telegram ходит к своим дата-центрам через
WebSocket/TLS, что для DPI выглядит обычным HTTPS. Стороннего сервера не требуется.

**Это самостоятельная реализация**, а не форк чужого кода: один файл на 2.5 тысячи строк,
только стандартная библиотека Go (см. `go.mod` — ни одной внешней зависимости). Идея взята
у [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) (@Flowseal), код — свой.
Лицензия — GPL-3.0, как и у всего приложения (см. `../../LICENSE`).

Лежит в репозитории в том числе потому, что GPL требует публиковать исходники к
распространяемым бинарникам.

## Сборка

Собирается под каждую ABI отдельно и кладётся в `jniLibs-extra`, а не в `jniLibs`:
последнюю полностью перезаписывает `ndk-build` при каждой сборке.

```sh
cd mobile/tools/tgws-proxy

# arm64-v8a
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath -ldflags "-s -w" \
  -o ../../app/src/main/jniLibs-extra/arm64-v8a/libtgwsproxy.so .

# armeabi-v7a
CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7 go build -trimpath -ldflags "-s -w" \
  -o ../../app/src/main/jniLibs-extra/armeabi-v7a/libtgwsproxy.so .

# x86_64
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags "-s -w" \
  -o ../../app/src/main/jniLibs-extra/x86_64/libtgwsproxy.so .

# x86
CGO_ENABLED=0 GOOS=linux GOARCH=386 go build -trimpath -ldflags "-s -w" \
  -o ../../app/src/main/jniLibs-extra/x86/libtgwsproxy.so .
```

Расширение `.so` — не ошибка: так Android упаковывает и распаковывает файл вместе с
нативными библиотеками (в манифесте включён `extractNativeLibs`), после чего его можно
запустить как обычный исполняемый файл. По той же причине в `app/build.gradle.kts` стоит
`keepDebugSymbols` — штатный strip из NDK ломает Go-бинарники.

## Запуск

```
libtgwsproxy --host 127.0.0.1 --port 1443 --secret <hex32> --pool 8
```
