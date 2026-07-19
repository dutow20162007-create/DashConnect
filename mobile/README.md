# Dash Connect для Android

Мобильная версия [Dash Connect](../README.md) — обход DPI, VLESS/AmneziaWG и мост Telegram
в одном приложении, с тем же интерфейсом, что и десктопная версия.

> **Это модифицированный форк [ByeDPIAndroid](https://github.com/dovecoteescapee/ByeDPIAndroid)**
> (© dovecoteescapee), распространяемый под GPL-3.0. Изменено существенно: интерфейс,
> DPI-пресеты, автоподбор стратегии, движок VLESS/AmneziaWG, мост Telegram, апдейтер.
> Полный разбор происхождения и лицензий — в [NOTICE.md](NOTICE.md).

## Что умеет

- **Обход DPI** — локальный SOCKS5 на базе [ByeDPI](https://github.com/hufrea/byedpi),
  трафик заворачивается в него через VpnService. 21 готовая стратегия под разных провайдеров.
- **Автоподбор** — по очереди поднимает стратегии на отдельном порту и проверяет, проходит ли
  через них настоящий TLS-хендшейк до заблокированных адресов. Берёт первую рабочую.
- **VPN** — VLESS и AmneziaWG на движке sing-box, если обхода DPI не хватает.
- **Telegram** — локальный MTProto-мост поверх WebSocket/TLS, без стороннего сервера.
- **Обновления** — проверка релизов на GitHub и установка прямо из приложения.

## Сборка

```sh
git clone https://github.com/dutow20162007-create/DashConnect
cd DashConnect/mobile
./gradlew assembleDebug
```

APK появится в `app/build/outputs/apk/debug/`.

Подмодули подтягивать **не нужно** — весь нативный код лежит в дереве.

Требуется:

| Компонент | Версия |
|-----------|--------|
| JDK | 17 |
| Android SDK | 34 |
| NDK | 27.1.12297006 |
| CMake | 3.22.1 |

Версии закреплены в [`app/build.gradle.kts`](app/build.gradle.kts). Готовый APK каждой версии
лежит в [релизах](https://github.com/dutow20162007-create/DashConnect/releases).

## Лицензия

GPL-3.0 — см. [LICENSE](LICENSE). Происхождение всех компонентов и их лицензии
разобраны в [NOTICE.md](NOTICE.md).

---

<p align="center">
  <a href="https://t.me/HUGOVSYKAYA"><b>t.me/HUGOVSYKAYA</b></a>
</p>
