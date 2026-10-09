# PHOSPHOR

ASCII-скринсейвер для Windows 10/11: сцены из текстовых символов (матрица, звёздное поле, трубы, огонь, жизнь, аквариум, телетекст, погода и др.), пропускаются через CRT/фосфор/VHS-фильтры. Реагирует на звук, подтягивает новости и погоду из сети (с кешем). Написан на C# / .NET 8, рисует через OpenGL.

## Быстрый старт

Требования: .NET 8 SDK (для сборки), Windows 10/11 x64.

```powershell
# 1. Сборка в dist\Phosphor (Phosphor.exe + Phosphor.scr), с проверкой --shot
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

# 2. Установка как скринсейвер текущего пользователя (без прав администратора)
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -TimeoutMinutes 5

# Посмотреть, что сделает установка, без изменений
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -WhatIf

# Удаление (восстанавливает прежнюю заставку; -KeepConfig оставляет phosphor.json)
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -KeepConfig
```

- `publish.ps1 -SelfContained` — самодостаточная сборка (папка ~70 МБ, .NET не нужен).
- `install.ps1` копирует файлы в `%LOCALAPPDATA%\Programs\Phosphor\`. Существующий `phosphor.json` не перезаписывается: новый дефолт кладётся рядом как `phosphor.default.json`. Прежние значения реестра (`HKCU\Control Panel\Desktop`: SCRNSAVE.EXE, ScreenSaveActive, ScreenSaveTimeOut) сохраняются в `previous-settings.json`.
- Настройки заставки: `control desk.cpl,,@screensaver`.

Проверка режима превью (маленькое окно в панели управления):

```powershell
dotnet build tools\PreviewHost -c Release
dotnet tools\PreviewHost\bin\Release\net8.0-windows\PreviewHost.dll dist\Phosphor\Phosphor.scr dist\preview.png
```

## Режимы запуска

| Аргумент | Что делает |
|---|---|
| `/s` | Полноэкранная заставка на всех мониторах (выход по вводу, движение мыши дольше порога `exit.mouseThresholdPx` выключает её) |
| `/p <HWND>` или `/p:<HWND>` | Превью в окне панели управления (без сети и звука). Вызывает Windows |
| `/c` | Настройки. Пока открывает `phosphor.json` в редакторе |
| `/w` | Обычное окно 1280×720 для отладки |
| `--shot <файл.png>` | Один кадр в PNG и выход (для проверки без показа) |

Параметры, которые работают с `/w` и `--shot`:

| Опция | Пример | Значение |
|---|---|---|
| `--scene <id>` | `--scene matrix` | Сцена (по умолчанию из конфига) |
| `--preset <имя>` | `--preset amber` | Пресет фильтров |
| `--frames N` | `--frames 60` | Сколько кадров отрисовать перед снимком |
| `--size WxH` | `--size 1920x1080` | Размер окна или кадра |
| `--seed N` | `--seed 42` | Зерно генератора случайных чисел |

Пример: `Phosphor.exe --shot shot.png --scene pipes --preset rave --size 1280x720`

Переменные окружения для `--shot` (воспроизводимые снимки и проверка состояний):

| Переменная | Пример | Значение |
|---|---|---|
| `PHOSPHOR_SHOT_TIME` | `14:00` | Время дирижёра (день/ночь). По умолчанию 04:20 — ночь |
| `PHOSPHOR_CACHE_DIR` | `.build\satcache` | Каталог кеша данных вместо `%LOCALAPPDATA%\Phosphor\cache` (пустой каталог = «первый запуск») |
| `PHOSPHOR_FAKE_BPM` | `128` | Синтетический бит; дирижёр меняет сцены по фразам |
| `PHOSPHOR_OVERLAY` | `hud` | Включить оверлей в снимке |
| `PHOSPHOR_SWITCH_AT` / `_TO` / `_KIND` | `120` / `plasma` / `vhsnoise` | Смена сцены на кадре N — проверка переходов (`_KIND`: fade, vhsnoise, glitch, cut) |

## Конфиг `phosphor.json`

Файл лежит рядом с exe (`%LOCALAPPDATA%\Programs\Phosphor\phosphor.json` после установки). Отсутствующие поля получают значения по умолчанию. Битый файл — предупреждение в логе и встроенные настройки.

| Ключ | По умолчанию | Описание |
|---|---|---|
| `cellHeightPx` | `18` | Высота ячейки сетки в пикселях, 8..64 |
| `scenes.enabled` | все сцены | Список id включённых сцен. Неизвестные id пропускаются с предупреждением |
| `scenes.speed` | `1.0` | Общая скорость сцен, 0.05..10 |
| `scenes.palette` | `"default"` | Палитра: `default`, `amber`, `mono`, `rainbow` |
| `scenes.params` | `{"matrix":{"density":0.6}}` | Параметры сцен по id. Имена параметров — в исходниках сцены |
| `filters.active` | `"terminal"` | Имя активного пресета |
| `filters.presets` | см. ниже | Пресеты: имя → массив стадий фильтров. Стадия: `{"id": "...", <параметры>, "mod": {...}}` |
| `filters.presets.*.mod` | — | Модуляция параметра стадии сигналом: `{"параметр": {"src": "...", "amount": N}}`. Источники: звук — `level`, `bass`, `mid`, `high`, `beat`, `beatPulse`; состояние — `breath`, `night`, `fatigue`, `transition` |
| `shuffle.scenes` | `true` | Менять сцены по таймеру |
| `shuffle.presets` | `true` | Менять пресеты фильтров по таймеру |
| `shuffle.intervalSec` | `90` | Интервал смены, минимум 5 с |
| `shuffle.transition` | `"vhs-noise"` | Переход: `fade`, `vhs-noise`, `cut` |
| `shuffle.transitionSec` | `1.5` | Длительность перехода, 0..10 с |
| `shuffle.perMonitor` | `true` | Разная сцена на каждом мониторе |
| `shuffle.presetOnDrop` | `true` | Новый пресет при смене сцены |
| `shuffle.randomFilterChains` | `false` | Случайные цепочки фильтров вместо пресетов (не рекомендуется) |
| `audio.enabled` | `true` | Захват звука для реакции сцен |
| `audio.sensitivity` | `1.0` | Чувствительность к звуку |
| `audio.maxPulse` | `0.8` | Потолок вспышки `beatPulse` |
| `audio.minBeatIntervalMs` | `250` | Минимальный интервал между ударами |
| `network.enabled` | `true` | Сеть: новости, погода, телетекст. Выкл. — только кеш |
| `network.city` | `"Москва"` | Город для погоды (любое название, геокодер Open-Meteo) |
| `network.news` | `["tass","bbc","reddit:worldnews"]` | Источники новостей, см. ниже |
| `network.teletext` | `["svt","nos"]` | Каналы телетекста: `nos`, `svt` |
| `exit.mouseThresholdPx` | `12` | Сколько пикселей движения мыши выключают заставку |

Ключа `network` в текущем `phosphor.json` нет — действуют значения по умолчанию из таблицы.

Источники новостей (`network.news`): `tass`, `tass-en`, `ria`, `interfax`, `bbc`, `reuters`, `reddit:<сабреддит>` (например `reddit:worldnews`).

## Сцены и фильтры

Сцены (`scenes.enabled`): `matrix`, `starfield`, `pipes`, `doomfire`, `life`, `dvd`, `aquarium`, `boids`, `grayscott`, `teletext`, `teletype`, `torus`, `voxel`, `weatherboard`.

Фильтры (стадии пресетов): `crt`, `phosphor`, `vhs`, `chroma`.

Встроенные пресеты: `clean` (только CRT), `terminal` (зелёный фосфор + CRT), `amber` (янтарный фосфор), `kassette` (VHS + хроматическая аберрация), `rave` (пульсация под бит).

## Сеть и Fort Firewall

Новости, погода и телетекст качаются в фоне и кешируются. Превью (`/p`) сеть не использует.

Если установлен Fort Firewall, новый exe может не получить доступ в сеть: в логе будет ошибка `10013` (запрещён доступ к сокету). Разрешите сеть для полного пути:

`%LOCALAPPDATA%\Programs\Phosphor\Phosphor.scr`

(и `Phosphor.exe`, если запускаете его напрямую.) Без сети сцены работают на кеше или без данных.

## Удаление

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -KeepConfig
```

Скрипт восстанавливает прежние значения заставки из `previous-settings.json`, удаляет папку установки и спрашивает подтверждение. Без `-KeepConfig` удаляется и `phosphor.json` со всеми настройками.

## Логи

- `%TEMP%\phosphor.log` — лог последнего запуска. Если файл занят другим процессом, пишется `%TEMP%\phosphor-<pid>.log`.
- Читать: `Get-Content "$env:TEMP\phosphor.log" -Encoding utf8 -Tail 50`.
- Окно с сообщением об ошибке показывается в режимах `/w` и `--shot`.

## Структура репозитория

- `src\` — исходники приложения (`Phosphor` — точка входа, остальные проекты — библиотеки).
- `scripts\` — `publish.ps1`, `install.ps1`, `uninstall.ps1`.
- `tools\PreviewHost\` — проверка превью `/p`, в solution не входит.
- `dist\` — результат сборки (не в git).
