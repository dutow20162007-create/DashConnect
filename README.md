<p align="center">
  <img src="docs/banner.png" alt="Dash Connect" width="100%">
</p>

<h1 align="center">Dash Connect ⚡</h1>

<p align="center">
  <b>Обход блокировок РФ — Discord, YouTube, Telegram, игры — в одну кнопку. Без VPN.</b>
</p>

<p align="center">
  <a href="https://t.me/HUGOVSYKAYA"><img src="https://img.shields.io/badge/Telegram-%40HUGOVSYKAYA-2CA5E0?style=for-the-badge&logo=telegram&logoColor=white" alt="Telegram-канал"></a>
  &nbsp;
  <a href="https://github.com/dutow20162007-create/DashConnect/releases/latest"><img src="https://img.shields.io/badge/%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C-.MSI-22C55E?style=for-the-badge&logo=windows&logoColor=white" alt="Скачать"></a>
  &nbsp;
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-4B5563?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 10/11">
</p>

<p align="center">
  💬 Новости, обновления и поддержка — мой Telegram-канал <a href="https://t.me/HUGOVSYKAYA"><b>@HUGOVSYKAYA</b></a>
</p>

---

Нажми **Подключить** — и работают **Discord, YouTube, Telegram** и игровые серверы (Fortnite / Apex / Dead by Daylight / FACEIT). Приложение запускается от администратора, само подбирает рабочую стратегию обхода DPI и живёт в системном трее.

## Возможности

- **Одна кнопка** — «Подключить / Отключить» поднимает всё.
- **Автоподбор стратегии** — проверяет Discord/YouTube и берёт самый быстрый рабочий пресет.
- **Игры без VPN** — игровой трафик (порт 5222 + домены Epic / EA / FACEIT) идёт напрямую, родной пинг.
- **Telegram сам, в фоне** — локальный WebSocket-мост к серверам Telegram (для DPI это обычный HTTPS, без чужого сервера). Настраивается автоматически при первом подключении.
- **Само находит заблокированное** — `--hostlist-auto`: обходит любой заблокированный домен.
- **Шифрованный DNS (DoH)** — Cloudflare 1.1.1.1 при подключении, твой исходный DNS обратно при отключении.
- **MSI-установщик + авто-обновление** — ставится как обычная программа и сама предлагает новые версии.

## Установка

1. Скачай свежий **`DashConnect-*.msi`** → [Releases](https://github.com/dutow20162007-create/DashConnect/releases/latest).
2. Установи и запусти **от имени администратора** (нужно для WinDivert).
3. Нажми **Подключить**.

Обновления прилетают сами — при запуске приложение проверяет GitHub и предлагает поставить новую версию.

## Как это работает

Блокировка в РФ идёт двумя способами, и Dash Connect закрывает оба — без VPN.

**1. DPI-десинхронизация ([Zapret](https://github.com/bol-van/zapret) / winws).** Провайдер читает имя сайта в TLS (SNI) и рвёт соединение. `winws.exe` через драйвер WinDivert фрагментирует и «портит» пакеты так, что DPI не опознаёт соединение, а реальный сервер собирает его правильно. Dash Connect разбирает готовые пресеты Zapret и запускает winws с проверенными аргументами; для игр (порт 5222) — чистый `split` без fake-пакетов, чтобы не ломать игру.

**2. Telegram — WebSocket-мост.** Telegram ходит к дата-центрам по IP (MTProto), обычный DPI-обход на нём не срабатывает. Поэтому прога поднимает локальный мост (`tg-ws-proxy`) на `127.0.0.1:1443`, который оборачивает Telegram в WebSocket/TLS к его собственным серверам — для провайдера это выглядит как обычный HTTPS. Настройка идёт в фоне при первом подключении, дальше Telegram подключается сам. Чужой сервер не нужен.

**3. Шифрованный DNS.** Провайдер может отдавать фальшивый IP на DNS-запрос. Dash Connect переключает систему на Cloudflare 1.1.1.1 с DoH (подменить нельзя), а при отключении возвращает исходный DNS.

> **Предел:** то, что блокируется по IP целиком, без прокси обойти нельзя — пакет просто не доходит. Всё остальное (DPI + авто-детект + DoH + мост Telegram) прога закрывает.

## Благодарности

Dash Connect — это GUI-обёртка и автоматизация поверх открытых проектов. Вся заслуга по самому обходу DPI принадлежит их авторам:

- **[Zapret](https://github.com/bol-van/zapret)** (@bol-van) — движок обхода DPI (winws + WinDivert).
- **[zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)** (@Flowseal) — сборка с готовыми пресетами (1.9.9c), на её основе кастомные пресеты.
- **[tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)** (@Flowseal) — WebSocket-мост для Telegram.
- **[WinDivert](https://github.com/basil00/WinDivert)** (@basil00) — драйвер перехвата пакетов.
- **[sing-box](https://github.com/SagerNet/sing-box)** (SagerNet) — опциональный VPN-туннель (по умолчанию выключен), GPL-3.0.

Телефонная версия (папка [`mobile/`](mobile/)):

- **[ByeDPIAndroid](https://github.com/dovecoteescapee/ByeDPIAndroid)** (@dovecoteescapee) — основа мобильной версии, она является его модифицированным форком. **GPL-3.0.**
- **[ByeDPI](https://github.com/hufrea/byedpi)** (@hufrea) — SOCKS5-ядро обхода DPI, MIT.
- **[hev-socks5-tunnel](https://github.com/heiher/hev-socks5-tunnel)** (@heiher) — туннель TUN→SOCKS5, MIT (внутри — lwip под BSD-3-Clause и libyaml под MIT).

> **Лицензии.** Весь проект распространяется под **GPL-3.0** — полный текст в корневом
> [`LICENSE`](LICENSE). Код в `mobile/` — производная работа от ByeDPIAndroid (тоже GPL-3.0),
> разбор происхождения каждого компонента — в [`mobile/NOTICE.md`](mobile/NOTICE.md). Десктопная
> часть бандлит sing-box (GPL-3.0), поэтому распространяется на тех же условиях.

> Автор одного из проектов и хочешь изменить формулировку или убрать упоминание — открой issue.

---

<p align="center">
  <a href="https://t.me/HUGOVSYKAYA"><b>t.me/HUGOVSYKAYA</b></a>
</p>
