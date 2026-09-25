# Pult — полный бриф проекта для ИИ

**Снимок: версия 0.14.1, 25.09.2026.** Файл самодостаточен: после него ИИ видит весь
проект — что за продукт, из чего состоит, как устроен, что сейчас в рабочем состоянии
и как проверять. Даты/цифры ниже — факты на момент снимка.

**Читать в таком порядке:** этот файл (`BRIEF.md`) → `PROJECT.md` (карта и паттерны) →
`CHANGELOG.md` (история по версиям, свежее сверху) → `HANDOFF.md` (последние сессии,
ловушки, что не трогать).
**Полный код целиком:** `transfer-codebase.txt` (60 файлов, ~844 тыс. символов),
порциями для чата — `transfer-part-1..10.txt`. Пересобрать: `python mktransfer.py`.

---

## 1. Что это за продукт

Десктоп-помощник Windows **«Пульт»** — WPF-приложение, тёмный ретро-стиль.
- **Стек:** WPF, `net10.0-windows`, C# 12, Nullable включён, `ImplicitUsings`.
  Один NuGet: `LibreHardwareMonitorLib 0.9.6` (датчики). Свои шрифты в `Pult/Fonts/*.ttf`
  (`PressStart2P` = ретро, `MaterialIcons` = глифы) — подключены как `<Resource>`.
- **Объём кода:** 45 `.cs` (15 392 строки) + 15 `.xaml` (5 295 строк) = **20 687 строк**
  (без `bin/obj`). Самые тяжёлые: `SystemView` 3084+1054, `MainWindow` 1383+259,
  `SettingsView` 707+1436, `SystemMonitor` 892, `GameService` 753, `LlmClient` 685.
- **Всё локально:** `%AppData%\Pult` (settings.json, actions.json, history.json),
  `%LocalAppData%\Pult` (логи, crash.log). Наружу — только запросы к LLM и web-инструменты.
- **Точка входа:** `Pult/App.xaml(.cs)` → `Appearance.Init` + `ThemeService.Apply` →
  гейт админа `AdminGate` → мастер `WelcomeWizard` (первый запуск) → `MainWindow`.
- **Запуск разработчиком:** VS/F5 (Debug). Перед `dotnet build` убедиться, что `Pult.exe`
  не запущен (иначе MSB3027/MSB3021). Сборки: Debug и Release, обе должны быть **0/0**.

---

## 2. Карта исходников

### Корень `Pult/`
| Файл | Строк | Что это |
|---|---|---|
| `App.xaml` | 484 | Все ресурсы: стили/шаблоны, шрифты `F_Ui`/`F_Pixel`, кисти `B_*` (в т.ч. `B_Muted` стр.27), углы |
| `App.xaml.cs` | 72 | Startup: `Appearance.Init`, `ThemeService.Apply`, порядок окон |
| `MainWindow.xaml` | 259 | Каркас: сайдбар-навигация (7 кнопок, `Tag` = ключ вьюхи), `ViewHost`, статус-строка, чип версии, hero-блик `HeroSheen` |
| `MainWindow.xaml.cs` | 1383 | Навигация `GoTo`/`ShowView`, экран «Действия», hero (кольца CPU/RAM/диск, «Пульс»), `ApplyAppearance` (тема/эффекты/анимации бликов) |
| `Ring.cs` | 153 | Свой контрол кольца: DP `Thickness`/`Segments`/`Fill`; `Value=100` рисуется кругом |
| `AdminGate.xaml(.cs)` | 125+71 | Проверка прав администратора |
| `WelcomeWizard.xaml(.cs)` | 259+104 | Мастер первого запуска |
| `PatchesDialog.xaml(.cs)` | 76+114 | Окно «Патчи» — таймлайн из `Services/ReleaseNotes` |
| `ConfirmDialog.xaml(.cs)` | 141+53 | `Ask()` — подтверждение действия |
| `ResultDialog.xaml(.cs)` | 179+83 | `ShowResult()` — итог операции |
| `WidgetWindow.xaml(.cs)` | 107+176 | Виджет поверх окон (часы+CPU/RAM), живёт в `ScaleTransform` под масштаб |
| `AssemblyInfo.cs` | 11 | атрибуты |
| `Pult.csproj` | 22 | `Version=0.14.1`, `OutputType=WinExe`, `AllowUnsafeBlocks`, ресурс `Fonts/*.ttf` |

### `Pult/Views/` — экраны (7 файлов; экран «Действия» живёт в `MainWindow`)
| View | Файлы | Что видит юзер |
|---|---|---|
| `SimpleHomeView` | 716+373 | Главная **Простого** интерфейса: светофор, «Проверить всё», 4 кнопки-цепочки |
| `SystemView` | 3084+1054 | **Система**: hero с 4 КПИ-плитками `HeroRow` (0.14.0 вместо колец) + 6 вкладок (Обзор/Скорость/Хранилище/Твики/Железо/Сеть), 21 плашка, автоколонки по высоте |
| `SettingsView` | 707+1436 | **Настройки**: Модель LLM; **Вид** (Эффекты, Интерфейс, **Шрифт**, **Масштаб** — 0.14.0); Персонализация (4 пресета + свои цвета); Экспериментальное; Автопорядок Загрузок; доп. папки игр; диагностика/бэкап; 11 полос-разделителей `Height=2` |
| `ChatView` | 775+493 | Чат с LLM: пушири, markdown-лайт, вызовы инструментов, история `history.json` |
| `GamesView` | 599+193 | Игры: Steam + ярлыки + папки, поиск/сорт/«Случайная», запуск |
| `HistoryView` | 179+77 | Журнал действий с откатом + точка восстановления |
| `WidgetsView` | 65+39 | Управление виджетом (вкл/положение/поверх) |
| *(навигация)* | — | Сайдбар: **Действия / Ассистент / Игры / Система / Настройки / История / Виджеты**; в Simple-режиме «Действия» заменяются домом, «Система» скрыта |

### `Pult/Services/` — все static, все методы try/catch → строка результата
| Группа | Сервисы (строки) |
|---|---|
| **Железо/система** | `SystemMonitor` 892 (CPU/RAM/диски/сеть/процессы/автозагрузка/программы/мониторы/адаптеры), `SensorsService` 204 (LibreHardwareMonitor), `HardwareService` 265 (обзор железа), `ExtraInfoService` 587 (Wi-Fi/TCP/аудио/обновления/ошибки) |
| **Файлы/чистка** | `DiskCleanupService` 476 (дубликаты SHA-256, Temp, тяжёлые, корзина, DISM), `FileService` 67 (автопорядок Загрузок) |
| **Игры** | `GameService` 753 (скан Steam/ярлыков/папок, кэш, `Game`-рекорд), `GameArt` 135 (обложки/иконки) |
| **Твики/приватность** | `TweakService` 392 (реестр: `GetState`→on/off/na, `Apply`→строка), `PrivacyService` 301 (приватность, схемы питания, часы, P2P) |
| **Пакеты/процессы** | `WingetService` 174, `AppxService` 73, `Proc` 53 (запуск с таймаутом) |
| **LLM/веб** | `LlmClient` 685 (OpenAI-совместимый API, tools), `WebService` 198 (поиск/чтение ссылок) |
| **Оформление** | `ThemeService` 368 (4 пресета + свои HEX, полная палитра производных, `Changed`), `Appearance` 123 (шрифт `ApplyFont` + **масштаб `ApplyScale`/`TextMode`**), `SheenFx` 147 (анимации бликов: `Breathe`/`Sweep`/`AnimateBars`), `EffectsHelper` 155, `DialogFx` 30, `DesignTokens` 35 |
| **Состояние/журнал** | `AppSettings` 207 (JSON + оси + миграции), `ActionJournal` 186 (`actions.json`, откат), `AppLog` 124, `CrashLog` 49, `ReleaseNotes` 163 |
| **Разное** | `PulseService` 167 (скоринг «Пульса», ЭКГ), `RestorePointService` 38 (точка восстановления) |

Рекорды-типы живут рядом с сервисами: `Game` (в `GameService.cs`), `DiskInfo`,
`ProgramInfo`, `AdapterInfo`, `MonitorInfo`, `StartupEntry` (в `SystemMonitor.cs`),
`ChatMessage`/`ToolCall` (в `LlmClient.cs`), `Preset` (в `ThemeService.cs`).

---

## 3. Данные, оси, гейты

**Файлы:** `%AppData%\Pult\settings.json` (настройки), `actions.json` (журнал откатов),
`history.json` (чат); `%LocalAppData%\Pult\` — логи/`crash.log`.
Каталог настроек читается через `AppSettings.Dir`: **сначала переменная `%APPDATA%`
напрямую**, фолбэк — `GetFolderPath` (иначе тестовые оверрайды не доходили до .NET 10).

**`settings.json` (схема):** `Llm{Type,BaseUrl,ApiKey,Model}` (по умолч. Ollama
`http://127.0.0.1:11434/v1`, модель `qwen2.5:7b`), `ExperimentalEnabled`,
`ExtraGameDirs[]`, `AutoOrganizeEnabled/IntervalMin/Dirs[]`,
`Effects` (0..3), `Interface` (0..1), `StrictPrivacyMode`,
`WidgetLeft/Top/Visible/Topmost`, `AccentColor` (`#7C6CF0`),
`BackgroundColor` (`#0A0E1A`), `PanelColor` (`#0E1424`),
`FontKey` (`segoe|retro|bahnschrift|trebuchet`), `UiScale` (зажат 0.75..1.75).

**Оси и гейты:**
- `Effects`: Auto(детект: `RenderCapability.Tier`, `ClientAreaAnimation`, батарея) /
  Minimum / Normal / Maximum. Читать **только** через `AppSettings.EffectiveEffects()`.
  Пороги Auto вынесены в чистую `AppSettings.DecideEffects(tier, anim, battery)`
  (0.14.2): `tier<2 || !anim → Minimum; батарея → Normal; иначе Maximum`.
  Спека зашита в режим стенда `auto` — меняешь пороги, `render_adv auto` падает.
- `Interface`: Simple (дом вместо Действий, нет Системы) / Advanced.
- `AppSettings.ExpOn()` = флаг `ExperimentalEnabled` **и** Advanced — опасные кнопки,
  чат и удаления идут только через него.
- `StrictPrivacyMode`: вырезает `get_clipboard` из tools, без `history.json`,
  стирает историю+лог при выходе.

**Тема:** `ThemeService.Apply()` в `App.OnStartup` до первых окон; палитра —
производные от акцента/фона (`C_*` → `B_*`, строки-пары в `ApplyColors`);
светлая тема — пресет «Снег». Цвета в XAML — **живые `DynamicResource`**
(`B_Accent/B_Bg/B_Panel/B_Soft/B_Tile/B_Muted/...`), статика только для неизменяемого.

**Шрифты:** правило 0.14.2 — **весь текст идёт через `F_Ui`** (`DynamicResource`;
заголовки, лейблы, навигация, code-behind — `SetResourceReference`, не
`FindResource`, чтобы обновлялось live). `F_Pixel` — только ресурс
ретро-начертания (`ApplyFont("retro")` ставит `F_Ui → F_Pixel`) и превью
«Аа Привет» чипа «Ретро» в `SettingsView:565` — больше нигде. Смена шрифта
при FontKey=retro даёт пиксель-в-пиксель как раньше. Переключение —
`Appearance.ApplyFont`. **Ретро-правило:** Press Start 2P = ровно 1.0em/знак,
поэтому у текстовых кнопок только `MinWidth` (не `Width`) — контент
растягивает кнопку, а не режется (сконвертировано 33, аудит `retro_metrics.py`);
`○●■` вне cmap — WPF-fallback, норма. Наследование шрифта несут корневые
`TextElement.FontFamily="{DynamicResource F_Ui}"` (MainWindow:12, все диалоги)
— headless-вьюха в стенде получает его программно, прод не трогать.

**Масштаб:** `Appearance.ApplyScale` кладёт `LayoutTransform` контенту и пишет
`TextOptions.TextFormattingMode` (**Display при ≈100%, Ideal при 90–150%**) +
`TextRenderingMode=ClearType` на окно и content root. Локальные
`TextFormattingMode="Display"` из Views-XAML **удалены** — не возвращать (см. §7).

---

## 4. LLM-часть

`LlmClient`: OpenAI-совместимый API (Ollama либо свой `BaseUrl`+`ApiKey`),
`RunConversationAsync` + `ExecuteTool`, стрим-диалог в `ChatView`.
**18 инструментов:** `disk_space, organize_downloads, find_duplicates, clean_temp,
large_files, get_clipboard, startup_list, installed_programs, battery_status,
empty_recycle_bin, list_games, launch_game, system_info, list_processes, kill_process,
web_search, web_search_read, open_url`.
`ReadOnlyTools` (без подтверждения): `disk_space, list_games, system_info,
list_processes, recall_facts, web_search_read, get_clipboard, startup_list,
installed_programs, battery_status` — всё остальное через `ConfirmDialog`.
Строгий приват-режим убирает `get_clipboard` ещё на этапе сборки списка tools.

---

## 5. Паттерны (как здесь принято писать)

- **Действие юзера:** `ConfirmDialog.Ask()` → `Task.Run` (работа) → `Dispatcher.Invoke`
  (обновить UI) → `ResultDialog.ShowResult()` + `ActionJournal.AddX()` для изменений.
- **Результат сервиса — строка:** `"Готово..."` успех / `"Ошибка..."` провал, **никогда
  null**; `TweakService.Apply` после записи перепроверяет `GetState`.
- **Медленные проверки** (winget/pnputil/appx) — сторожа ~30с, опоздавшие данные
  дорисовываются; кэш игр: `GetGames(false)` из кэша, «Обновить» = `force=true`.
- **Свойства-контролы:** `Thickness`/`Segments`/`Fill` у `Ring` — DependencyProperty;
  `Value=100` = полный круг (clamp в coerce).
- **Кастомный `ComboBox`** обязателен для выбора (иначе SelectionBox — чёрный текст).
- **Свечение:** `DropShadowEffect` на элементе **с текстом = мыло** — только на рамках/
  кольцах без текста; один экземпляр эффекта = один элемент (это же ломало свечения колец).
- **Новые .cs подключаются автоматически** (SDK-проект), версия правится только в
  `Pult.csproj` `<Version>`; при бампе **обязательно** добавить запись в
  `ReleaseNotes.cs` (`IsCurrent` маркирует «(текущая)» по InformationalVersion).

### Табу (горький опыт)
1. **НЕ править файлы через PowerShell** — ломает UTF-8/кириллицу; многострочные XAML/CS
   правки делать байт-точными python-скриптами; проверять BOM/окончания после правок
   (`App.xaml`, `Pult.csproj` — BOM+CRLF; `ReleaseNotes.cs` — без BOM/LF;
   `MainWindow.xaml` — BOM+LF; прочий XAML обычно LF/BOM).
2. **Не перетирать настройки юзера** (см. §9) — тесты гоняются на копии через
   `APPDATA`-подмену.
3. **Не возвращать локальный `TextOptions.TextFormattingMode`** в Views-XAML.
4. **Не добавлять `AutoReverse`** на позиционные анимации — 0.14.0 убрал «виляние»
   по фидбеку; `audit_no_wiggle` это ловит.
5. **Не писать мусор в `FontKey`** — теги чипов это индексы, ключ берётся из
   `Appearance.Fonts[]`.
6. PowerShell гасит кириллицу в stdout: логи читать python-ом в UTF-8-файлы
   (вывод `python -c` с кириллицей падает на cp1251, в т.ч. на символе `→`);
   многострочный `python -c` из PS ломается на кавычках — писать скрипт в файл.

---

## 6. Харнесс проверок (где что и как гонять)

Всё лежит в `C:\Users\oleg\AppData\Local\Temp\opencode\` (не в репозитории).

**Стенд** — консольный проект `stand\` (ссылается на Pult): headless-рендер любого
экрана в PNG + живые режимы. Драйвер — `render_adv.py`:
```
python render_adv.py <режим> [K=V ...]     # оверрайды пишутся в копию настроек (advdata),
                                            # реальный settings.json не трогается (ассерт после)
```
**Режимы стенда:** экраны `main|home|system|chat|games|settings|history|widgets` (headless,
пишет `render_<имя>.png` + `stand_<имя>.log`), `settingsTall` (окно 2400px),
`light` (светлая тема), `themetest` (смена темы на живом окне), `live*` (показанное окно +
прокачка диспетчера), **`breathe`** (живой MainWindow: сэмплы `heroOp/heroX/cardOp/cardX`),
**`breathe-s`** (живое окно Настроек: `barOp/glintX`), **`fontclick`** (13 проверок
переключения шрифта + неизменность файла юзера), **`fxswitch`** (смена Эффектов
на лету: Maximum 8/8 полос → Minimum 0/B_Bar A=0/блик не запущен → возврат 8/8;
цепочка = Save → `ThemeService.Apply` → `mw.ApplyAppearance`), **`auto`**
(спека `DecideEffects` на 16 комбинаций tier×anim×bat). Оверрайды для рендеров: `UiScale`,
`FontKey`, `AccentColor`, `Effects`, `Interface`, а для честных A/B текста —
`STAND_TFM`/`STAND_TRM` (TextFormattingMode/TextRenderingMode до отрисовки).

**Пайплайн контроля пакета (порядок важен):**
```
python run_renders.py         # 12 рендеров (BASE-пин: UiScale=1.0, FontKey=segoe,
                              #   тема #7C6CF0/#0A0E1A/#0E1424 — юзер на «Океане»)
python run_main_variants.py   # варианты main (base/min/scale/shrift) — сохраняет render_main_base.png
python check_014.py           # 11 PIL-чеков  → должно быть 11/11 ALL PASS
python verify_renders.py      # 15 маркеров   → должно быть 15/15
python run_audits.py          # audit_pult/reverse/glyphs/xaml/no_wiggle + verify_renders → audit_report.txt
python clip_check.py          # ретро: рендер Games FontKey=retro → 4 кнопки шапки
                              #   (текст полон/вписан) → должно быть 0 проблем
```
Особенности: после `run_main_variants` файл `render_main.png` = `main_min` (плоский),
нормальный main живёт в `render_main_base.png`; порядок JOBS важен
(`settingsTall_min` до `settingsTall`, иначе затирает `render_settingsTall.png`);
при FAIL рендера — перезапустить `render_adv.py`, **не** чинить продукт-код.

**Прочие скрипты:** `ab_*.py`/`sharp_*.py`/`fringe.py`/`blur_*.py` — A/B-метрики
чёткости текста; `ab_low.py` (+`ab_low_grid.png`) — A/B нижних масштабов
75/80/85 Display×Ideal, база Display@100; `report_imgs.py` — картинки ДО/ПОСЛЕ
для юзера; ретро-набор `retro_metrics.py` (метрика TTF + аудит Width),
`clip_check.py` (посадка текста в кнопки после `minwidth_buttons.py`),
`verify_chip_labels.py`/`fix_chip_labels.py` (подписи чипов масштаба);
куча разовых
`check_*.py`/`fix_*.py`/`audit_*.py` из прошлых сессий (эволюция, не грех посмотреть).

**Артефакты:** `render_*.png` (актуальные рендеры), `report_scale09.png` /
`report_scale125.png` (ДО=Display сверху, ПОСЛЕ=Ideal снизу), `audit_report.txt`,
`stand_*.log`, `t09_*/t125_*` (эталоны A/B по тексту), `inventory_raw.txt` (инвентарь).

---

## 7. Текущее состояние (25.09.2026)

- **Версия `0.14.3`** (`Pult.csproj`), запись 0.14.3 в `ReleaseNotes.cs`
  (маркер `IsCurrent` — запись ОБЯЗАТЕЛЬНА при бампе). Debug/Release/стенд —
  **0 ошибок / 0 предупреждений**.
- **Проверки:** `check_014` **11/11 ALL PASS**, `verify_renders` **15/15**,
  `fontclick` **13/13**, `fxswitch` **fails=0**, `auto` **16/16 спеки**,
  `clip_check` **PASS**, аудиты `run_audits.py` — все exit=0 (в т.ч.
  `xaml_audit MISSING KEYS: 0`, `audit_no_wiggle` — позиционных
  AutoReverse нет). Живость 8с: `heroOp 0.302..0.995`, `heroX −109..260`,
  `barOp 0.505..0.991`, `glintX −84..192`.
- **Дизайн-волна 0.14.3 (пункты 6/7/8 отчёта юзера, CHANGELOG 0.14.3):**
  6) SystemView — строка чипов (температура/диск/ошибки, клик по
  ошибкам раскрывает События), температура в подписи CPU, превью
  Событий «ещё N», диск/P2P без жаргона в подписях; 7) полосы Height=2
  — статус-цвет секций (SystemView `PaintBlip`/`PaintSectionStrips`),
  в Настройках/виджетах тихие `B_Border`, LlmStrip = статус проверки,
  Minimum → `B_Bar` (контракт fxswitch/breathe-s); 8) Простой — метка
  «Главная», крупнее/воздушнее, дом = 4 плитки-цепочки (чат/история —
  ссылками), спаркл скрыт. Пиксель-маркеры home: 2 ряда плиток
  (y=344/542, 564/767), 2 подчёркивания ссылок (y=796, 156+96px),
  −42px высоты пульс-карточки, unique 693→574.
- **Баг-репорт второго ИИ (5 пунктов) закрыт (CHANGELOG 0.14.2):**
  1) заголовки/лейблы теперь следуют шрифту (весь текст через `F_Ui`,
  111 замен в 14 XAML + code-behind; `F_Pixel` — только ресурс ретро и
  превью чипа); 2) чипы масштаба 75/80/85 добавлены (UG 5→4, два ряда),
  A/B чёткости: lap −39..−44% к Display@100, Ideal на 0.85/0.80 детальнее
  Display ~+7-10%, на 0.75 паритет, текст читаем (`ab_low_grid.png`);
  3) «блики в Минимуме» — НЕ воспроизводится (цитаты репорта не бьются,
  `BuildCards` внутри `ApplyAppearance:575` пересобирает карточки при
  смене Эффектов) + эмпирика режимом `fxswitch`; 4) пороги Auto явные
  (`DecideEffects`, карточка «Авто», спека стенда `auto`; предлагаемые
  репортом `Tier==1→Normal` НЕ приняты); 5) «дизайн шаблонный» — отложено
  по решению юзера.
- **Живые анимации подтверждены логами** (8с): герой `heroOp 0.303→0.995`,
  `heroX −109→260` (wrap), карточки `cardOp 0.353→0.999`; Настройки
  `barOp 0.506↔0.991`, `glintX −84→192` (wrap).
- **Фидбек 0.14.1 закрыт:** блики оживлены (`SheenFx`), выбор шрифта работает
  (баг `Font_Click` писал `"0".."3"`; миграция `"0"→retro` — в памяти), мыло масштаба убрано.
- **Настройки юзера — НЕ перетирать (факт от 25.09 позднего, сам
  переключал):** тема зелёная — Accent `#3FB950`, Bg `#070B07`,
  Panel `#0C120C`, `Effects=3`, `Interface=1` (Расширенный),
  `UiScale=0.85`, `FontKey=retro`, `ExtraGameDirs` 2 шт.,
  `WidgetVisible=false`. Рендеры идут в advdata-копию
  с BASE-пином (тема/шрифт/масштаб), реальный файл не трогается.
- **Известные инциденты в `%LocalAppData%\Pult\crash.log`** (исторические, не воспроизводятся
  на текущей сборке): 22.09 — XamlParseException `CornerRadius` в `SimpleHomeView`;
  24.09 16:48 — `Ресурс "B_Muted" не найден` + «Работа диспетчера приостановлена».
  Сейчас `B_Muted` объявлен (`App.xaml:27`) и пересобирается темой
  (`ThemeService.cs:116`), рендеры home проходят.
- **Не трогать (из HANDOFF):** Pulse-скоринг, откаты журнала, паблиш win-x64 от 21.09;
  DeprecationWarning Pillow — игнор; `audit_pult` даёт ложные срабатывания;
  первый запуск `live-*` после простоя может зависнуть — повторить.
- **Сборка может упасть, если запущен `Pult.exe`** (MSB3027) — сначала закрыть.

---

## 8. Что где лежит в корне репозитория

| Файл/папка | Что это |
|---|---|
| `PROJECT.md` | Карта проекта + паттерны/табу (читать вместе с этим файлом) |
| `CHANGELOG.md` | Журнал по версиям, свежее сверху (0.14.1 → 0.9.0) |
| `HANDOFF.md` | Сессии работы: что делали, ловушки, порядок прогонов |
| `BRIEF.md` | Этот файл — полный снимок для передачи ИИ |
| `Pult/` | Исходники приложения (`Views/`, `Services/`, `Fonts/`, `bin/obj`) |
| `mktransfer.py` | Собирает весь код в `transfer-codebase.txt` + `transfer-part-N.txt` |
| `transfer-codebase.txt`, `transfer-part-1..10.txt` | Дамп всего кода (60 файлов) для копирования в чат |
| `publish/win-x64/` | Паблиш-сборка **от 21.09 (0.12.x)** — для 0.14.1 не обновлялся |
| `Pult-0.9.0-win-x64.zip` | Старый архив 0.9.0 (61 МБ) — исторический |

---

## 9. С чего начать новой сессии

1. Прочитать `HANDOFF.md` сверху (последняя сессия — `-4. Пакет 0.14.1`).
2. Собрать: `dotnet build Pult\Pult.csproj -c Debug` и `-c Release` (обе 0/0).
3. Прогнать пайплайн из §6 и убедиться: 11/11, 15/15, аудиты 0.
4. Правки — точечные, с сохранением BOM/окончаний; после правок пересобрать и
   перегнать проверки. Историю фиксировать в `CHANGELOG.md`, ход — в `HANDOFF.md`
   (git в проекте нет).
