# SysWidget

**[Русский](#русский) · [English](#english)**

---

## Русский

**SysWidget** — компактный виджет для Windows: небольшая карточка на экране показывает загрузку оперативной памяти, процессора, дисков, видеокарты и сети, а также заряд мыши, клавиатуры и наушников. Работает без установки, без драйверов и без прав администратора.

### Возможности

- **Карточка** с живыми значениями ОЗУ, ЦП, каждого физического диска (SSD/HDD), ГП, сети и периферии. Любую строку можно скрыть.
- **Топ процессов** по клику на строку: кто грузит ЦП, память, диск, ГП или сеть; процесс можно завершить (с подтверждением).
- **Окно мониторинга** («▦»): общая картина сразу, без выбора действий. Слева «Состояние ПК» (обновления, свободное место, диски, автозагрузка, ошибки системы, время работы, утечки памяти) и кнопки «Очистить кэши», «Освободить память», «Диспетчер задач», «Центр обновления»; справа значения всех метрик и общий график за 2 мин, 15 мин, 1 час или 24 часа (история хранится на диске и переживает перезапуск); ниже карточки метрик, события Windows, журнал тревог и автозагрузка. В шапке колокольчик оповещений, время и дата. Оформление берётся из темы, как у карточки; на маленьком экране окно убирает второстепенное и не прячет карточки метрик и кнопки.
- **Вердикт «почему тормозит»**: одна фраза с виновником («Процессор загружен (81%): chrome — 40%») и кнопка «Закрыть».
- **Освобождение памяти** («⚡»): не трогает активное окно, подсказывает, кто держит больше всего памяти.
- **Оповещения** о перегрузке, тихие часы, значок в трее, счётчики трафика.
- **Диагностика** в настройках: автозагрузка, крупные папки, события Windows, очистка временных файлов.
- Темы: неон, тёмная, светлая, пиксель-арт, терминал. Языки: русский и английский.

### Требования

- Windows 10 (1903 и новее) или Windows 11.
- .NET Framework 4.8 (уже есть в этих версиях Windows).
- Права администратора **не нужны**, драйверы **не нужны**. Единственное необязательное исключение — «Расширенные данные» (температура и износ диска) по запросу UAC.

### Запуск

1. Скачайте архив `SysWidget-x.y.z-portable.zip` из раздела Releases и распакуйте в папку, доступную вам на запись (не в `Program Files`).
2. Запустите `SysWidget.exe`. Если Windows покажет «Windows защитила ваш компьютер», нажмите «Подробнее» → «Выполнить всё равно»: это стандартное предупреждение для файла без цифровой подписи.

Подробности по управлению и настройкам — в файле [`КАК ЗАПУСТИТЬ.txt`](КАК%20ЗАПУСТИТЬ.txt).

### Сборка из исходников

SDK не нужен, достаточно встроенного в Windows компилятора C#:

```powershell
.\build.ps1
```

Исходники — в папке `src`, устройство проекта описано в [`SysWidget — код с пояснениями.txt`](SysWidget%20—%20код%20с%20пояснениями.txt).

### Приватность

Программа не имеет телеметрии и не отправляет ваши данные. Сеть используется только по вашему действию: проверка обновлений в настройках (через штатные Windows Update и winget) и пинг узла, если вы сами его задали в `net.json`. Все настройки и история лежат у вас: `%LOCALAPPDATA%\SysWidget` (или рядом с exe в переносном режиме). Папку можно удалить в любой момент.

### Лицензия

Исходный код доступен по лицензии [PolyForm Noncommercial 1.0.0](LICENSE): использовать, изучать и изменять можно для некоммерческих целей. Для коммерческого использования нужно отдельное письменное разрешение автора. © 2026 Aleksey Sokolov.

---

## English

**SysWidget** is a compact Windows widget: a small on-screen card showing RAM, CPU, disk, GPU and network load, plus the battery level of your mouse, keyboard and headphones. It needs no installation, no drivers and no administrator rights.

### Features

- **Card** with live RAM, CPU, every physical disk (SSD/HDD), GPU, network and peripherals. Any row can be hidden.
- **Top processes** on click: what loads the CPU, memory, disk, GPU or network; a process can be ended (with confirmation).
- **Monitoring window** (“▦”): the whole picture at a glance, no choices to make. “PC health” on the left (updates, free space, disks, startup items, system errors, uptime, memory leaks) with buttons “Clean caches”, “Free memory”, “Task Manager”, “Windows Update”; on the right the values of every metric and one combined graph for 2 min, 15 min, 1 hour or 24 hours (history is stored on disk and survives restarts); below are metric cards, Windows events, an alert log and startup items. The header has an alerts bell, the time and the date. The look comes from the theme, like the card; on a small screen the window drops the secondary parts and never hides the metric cards or the buttons.
- **“Why is it slow” verdict**: one sentence naming the culprit (“CPU is busy (81%): chrome — 40%”) and a “Close” button.
- **Memory release** (“⚡”): leaves the active window alone and tells you who holds the most memory.
- **Alerts** on overload, quiet hours, tray icon, traffic counters.
- **Diagnostics** in settings: startup items, big folders, Windows events, temp-file cleanup.
- Themes: neon, dark, light, pixel art, terminal. Languages: Russian and English.

### Requirements

- Windows 10 (1903 or newer) or Windows 11.
- .NET Framework 4.8 (already included in these Windows versions).
- Administrator rights are **not** required, drivers are **not** required. The only optional exception is “Extended data” (disk temperature and wear), which asks for UAC.

### Running

1. Download `SysWidget-x.y.z-portable.zip` from Releases and unpack it to a folder you can write to (not `Program Files`).
2. Run `SysWidget.exe`. If Windows shows “Windows protected your PC”, click “More info” → “Run anyway”: this is the standard warning for an unsigned file.

Controls and settings are described in [`КАК ЗАПУСТИТЬ.txt`](КАК%20ЗАПУСТИТЬ.txt) (Russian).

### Building from source

No SDK needed, only the C# compiler built into Windows:

```powershell
.\build.ps1
```

Sources are in `src`; the project structure is described in [`SysWidget — код с пояснениями.txt`](SysWidget%20—%20код%20с%20пояснениями.txt) (Russian).

### Privacy

The program has no telemetry and does not send your data. The network is used only on your action: checking for updates in settings (via the built-in Windows Update and winget) and pinging a host if you set one yourself in `net.json`. All settings and history stay on your machine: `%LOCALAPPDATA%\SysWidget` (or next to the exe in portable mode). You can delete that folder at any time.

### License

Source-available under the [PolyForm Noncommercial 1.0.0](LICENSE) license: you may use, study and modify it for noncommercial purposes. Commercial use requires separate written permission from the author. © 2026 Aleksey Sokolov.
