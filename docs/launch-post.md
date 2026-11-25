# Launch posts

Replace `https://github.com/svinnayazalupa/pomodoro-taskbar` with the public GitHub URL before posting. Do not create or push the repository until the account, name, and license are confirmed.

---

## English — Reddit (r/Windows11, r/csharp)

**Title:** I built a tiny Pomodoro timer that lives on the Windows 11 taskbar

I wanted a Pomodoro clock I could glance at without leaving the window I'm in — so I put it on the taskbar itself.

Pomodoro is a ~160 KB WinForms app with three seven-segment counters and no labels: work in red, rest in green, repetitions in yellow. Right-click to change the times, lock it so it doesn't move, or start it with Windows. That's the whole UI.

The interesting bug was not the timer. An earlier build used `SetParent` to embed the strip as a child of Explorer's taskbar. That ties the two programs' input queues together, so a stall in my app froze the whole taskbar (event 1002, "explorer.exe stopped interacting"). A UI Automation walk of the bar every second quietly leaked about 2.5 GB over a few days. The current build never becomes a child of Explorer. It's a top-level, topmost, per-pixel-alpha window owner-docked to the taskbar — the same idea NetSpeedTray uses — and it stays visible when Start or Search opens.

It's free and open source (MIT): https://github.com/svinnayazalupa/pomodoro-taskbar

I'd love feedback, especially if you try it on an unusual taskbar setup (vertical bar, multiple monitors, StartAllBack, and the like).

---

## English — X / Telegram

A Pomodoro timer that lives on the Windows 11 taskbar: three seven-segment counters, no labels — work in red, rest in green, reps in yellow. Right-click for settings, lock it, or start it with Windows. ~160 KB, MIT.

I first embedded it into Explorer with SetParent. That was a mistake: it tied our input queues together and a stall froze the whole taskbar. The rewrite is a top-level topmost layered window, owner-docked to the taskbar, and it no longer takes Explorer down with it.

https://github.com/svinnayazalupa/pomodoro-taskbar — feedback welcome.

---

## Русский — Telegram / пост

Я сделал крошечный таймер Помодоро, который живёт на панели задач Windows 11.

Три семисегментных счётчика, без подписей: работа красным, отдых зелёным, повторы жёлтым. Правый клик — настройки, фиксация, автозапуск с Windows. Весь интерфейс — это и есть. Приложение весит около 160 КБ, лицензия MIT.

Самое любопытное было не в самом таймере. В первой версии полоска встраивалась в Проводник через SetParent. Так связываются очереди ввода двух процессов: любое подвисание таймера вешало всю панель задач (событие 1002, «explorer.exe перестал взаимодействовать»). Плюс раз в секунду обходилось дерево UI Automation панели — и за пару дней процесс тихо съедал около 2,5 ГБ. Сейчас это отдельное поверх всех, полупрозрачное окно, «привязанное» к панели как к владельцу, без SetParent. Пуск и Поиск полоску больше не прячут, а Проводник с ней больше не падает.

Исходники: https://github.com/svinnayazalupa/pomodoro-taskbar

Буду рад фидбеку — особенно если стоит вертикальная панель, несколько мониторов или что-то вроде StartAllBack.
