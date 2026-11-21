# Changelog / История изменений

## 1.1.0 — 2026-11-21

**RU**
- Окно мониторинга переработано: общая картина без выбора действий. Слева «Состояние ПК» (кольцо, семь проверок, кнопки очистки кэшей, освобождения памяти, диспетчера задач, центра обновления и настроек), справа значения метрик и общий график за 2 мин, 15 мин, 1 ч и 24 ч, ниже карточки метрик, события Windows, журнал тревог и автозагрузка. В шапке колокольчик оповещений, время и дата. На маленьком экране окно убирает второстепенное, но не прячет карточки метрик и кнопки; корректно разворачивается на весь экран.
- Новые темы: «Пиксель» (пиксель-арт, плоская, шрифт Courier New) и «Терминал» в палитре Kanagawa Wave. У «Терминала» своя раскладка окна мониторинга: график ЦП точками и шкалы по ядрам, память, диски, сеть и таблица процессов; названия блоков вписаны в рамки.
- После перезапуска виджета графики за 2 и 15 минут сразу заполнены сохранённой историей (если виджет не работал не дольше двух минут).
- Журнал сработавших тревог (alertlog.txt) и колонка времени в журналах без наложения текста на точки.
- Оформление окна берётся из темы, как у карточки; подписи и значения шкал выравниваются по самой длинной строке, текст не заходит на шкалы.

**EN**
- The monitoring window was redesigned: the whole picture without choices to make. “PC health” on the left (ring, seven checks, buttons for cleaning caches, freeing memory, Task Manager, Windows Update and settings), metric values and one combined graph for 2 min, 15 min, 1 h and 24 h on the right, then metric cards, Windows events, an alert log and startup items. The header has an alerts bell, the time and the date. On a small screen the window drops the secondary parts but never hides the metric cards and buttons; it maximizes correctly.
- New themes: “Pixel” (pixel art, flat, Courier New font) and “Terminal” in the Kanagawa Wave palette. Terminal has its own monitoring-window layout: a dotted CPU graph with per-core bars, memory, disks, network and a process table; block titles are set into the frames.
- After a restart the 2 and 15 minute graphs are filled from the saved history right away (if the widget was off for no more than two minutes).
- An alert log (alertlog.txt); the time column in the logs no longer lets text run under the dots.
- The window look comes from the theme, like the card; labels and values of the bars are aligned to the longest string, so text never runs onto the bars.

## 1.0.0 — первый выпуск / first release

**RU**
- Карточка нагрузки: ОЗУ, ЦП, физические диски (SSD/HDD), видеокарта, сеть, заряд мыши, клавиатуры и наушников.
- Топ процессов по клику на строку, завершение процесса с подтверждением.
- Окно мониторинга: графики за 2 мин, 15 мин, 1 ч и 24 ч, история сохраняется на диске; полоса «почему тормозит» с кнопкой «Закрыть»; подсказка при наведении на график.
- Освобождение памяти без подвисания активного окна, с указанием главного «едока» памяти.
- Оповещения о перегрузке и заряде, тихие часы, живой значок в трее, счётчики трафика.
- Диагностика: автозагрузка, крупные папки, события Windows, очистка временных файлов, обновления Windows и программ.
- Темы: неон, тёмная, светлая. Языки: русский и английский.
- Без драйверов и без прав администратора; переносной режим.

**EN**
- Load card: RAM, CPU, physical disks (SSD/HDD), GPU, network, battery of mouse, keyboard and headphones.
- Top processes on click, ending a process with confirmation.
- Monitoring window: graphs for 2 min, 15 min, 1 h and 24 h with history stored on disk; a “why is it slow” strip with a “Close” button; hover tooltip on graphs.
- Memory release that leaves the active window alone and names the biggest memory holder.
- Overload and battery alerts, quiet hours, live tray icon, traffic counters.
- Diagnostics: startup items, big folders, Windows events, temp-file cleanup, Windows and app updates.
- Themes: neon, dark, light. Languages: Russian and English.
- No drivers and no administrator rights; portable mode.