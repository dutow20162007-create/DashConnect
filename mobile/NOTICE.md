# Dash Connect (Android) — происхождение и лицензии

Это приложение — **модифицированный форк [ByeDPIAndroid](https://github.com/dovecoteescapee/ByeDPIAndroid)**
(© dovecoteescapee). От оригинала оно отличается сильно: свой интерфейс в стиле десктопной
версии, собственные DPI-пресеты и автоподбор стратегии, движок VLESS/AmneziaWG на sing-box,
локальный мост Telegram и проверка обновлений.

**ByeDPIAndroid распространяется под GNU General Public License v3.0, поэтому и эта версия
распространяется под GPL-3.0.** Полный текст — в файле [LICENSE](LICENSE). Исходный код
опубликован в том числе для выполнения требований GPL: всякий, кто получил APK, имеет право
получить и соответствующие исходники.

## Вендоренные сторонние компоненты

Раньше эти папки подключались как git-подмодули. Теперь их исходники лежат прямо в
репозитории — чтобы он клонировался и собирался одной командой, без `git submodule update`.
Лицензионные файлы перенесены вместе с кодом:

| Путь | Апстрим | Лицензия |
|------|---------|----------|
| `app/src/main/cpp/byedpi` | [hufrea/byedpi](https://github.com/hufrea/byedpi) | MIT (© 2024 hufrea) — `LICENSE` |
| `app/src/main/jni/hev-socks5-tunnel` | [heiher/hev-socks5-tunnel](https://github.com/heiher/hev-socks5-tunnel) | MIT (© 2022 hev) — `License` |
| `…/hev-socks5-tunnel/src/core` | hev-task-system | MIT (© hev) — `License` |
| `…/hev-socks5-tunnel/third-part/hev-task-system` | [heiher/hev-task-system](https://github.com/heiher/hev-task-system) | MIT (© hev) — `License` |
| `…/hev-socks5-tunnel/third-part/lwip` | [lwip](https://savannah.nongnu.org/projects/lwip/) | **BSD-3-Clause** (© 2001–2002 Swedish Institute of Computer Science) — `License` |
| `…/hev-socks5-tunnel/third-part/yaml` | [libyaml](https://github.com/yaml/libyaml) | MIT — `License` |

> Замечание об lwip: у него BSD-3-Clause, а не MIT, как у остальных — там есть пункт о
> запрете использовать имя правообладателя для продвижения продукта.
>
> Замечание о libyaml: файл `third-part/yaml/License` в апстриме hev-socks5-tunnel содержит
> только копирайт hev, без исходного копирайта libyaml (Kirill Simonov, Ingy döt Net).
> Это унаследовано от апстрима, а не внесено при переносе.

## Готовые бинарники

Два артефакта хранятся собранными, потому что их нельзя воспроизвести из этого репозитория —
нужны отдельные Go-тулчейны.

### `app/libs/libbox.aar` — движок sing-box

[sing-box](https://github.com/SagerNet/sing-box) (© 2022 nekohasekai, SagerNet),
**GPL-3.0** — текст лицензии рядом: [`app/libs/LICENSE.sing-box`](app/libs/LICENSE.sing-box).
Обеспечивает VLESS и AmneziaWG.

Собран **из неизменённого апстрима, тег `v1.11.15`**, через gomobile (форк SagerNet
`gomobile@v0.1.4`). Точная команда сборки записана в [ANDROID-VPN-PLAN.md](ANDROID-VPN-PLAN.md).
Поскольку код sing-box не менялся, соответствующий исходник — это апстрим по ссылке выше на
указанном теге.

### `app/src/main/jniLibs-extra/*/libtgwsproxy.so` — мост Telegram

**Самостоятельная реализация, не форк.** Один Go-файл на ~2.5 тысячи строк, только
стандартная библиотека, без внешних зависимостей. Идея взята у
[tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) (@Flowseal), код написан с нуля,
лицензия — GPL-3.0, как у всего приложения.

Исходник и инструкция по сборке лежат здесь же: [`tools/tgws-proxy/`](tools/tgws-proxy/).

### Чего в репозитории нет

`app/src/main/jniLibs/` не входит в репозиторий намеренно: эти `.so` пересобираются из
`app/src/main/jni/` через `ndk-build` при каждой сборке.

## Сборка

```
gradlew assembleDebug     # APK для теста -> app/build/outputs/apk/debug/
gradlew assembleRelease   # неподписанный релизный APK
```

Нужны JDK 17, Android SDK 34, NDK 27.1.12297006 и CMake 3.22.1 (версии закреплены в
`app/build.gradle.kts`). Подмодули подтягивать не нужно — весь нативный код уже в дереве.
