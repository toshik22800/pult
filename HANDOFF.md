# Handoff — что сделано за сессию (для другого ИИ)

Читать вместе с `PROJECT.md` (карта проекта) и `CHANGELOG.md` (журнал по версиям).
Код: WPF, `net10.0-windows`, C# 12, Nullable on. Всё локально, наружу — только LLM.
Сборки: Debug И Release (`dotnet build Pult/Pult.csproj -c <cfg>`), обе должны быть 0/0.
Юзер запускает через VS (F5 → Debug). Перед сборкой убедиться, что `Pult.exe` не запущен
(иначе MSB3027/MSB3021 — файл занят; был случай с PID 2896).

## Профильтровано по порядку (свежее сверху)

### -8. Пакет 0.14.5: первые окна, фикс сохранения, инструменты, git (26.09)
Детали в CHANGELOG 0.14.5. Версия `Pult.csproj` = 0.14.5, запись 0.14.5 в
`ReleaseNotes.cs` (`IsCurrent`). Конвейер честно прогнал после бампа:
Debug/Release/стенд 0/0/0, `check_014` 11/11, `verify_renders` 15/15,
аудиты **8×exit=0** (в `audit_report.txt`), `clip_check` fails=0,
`fontclick` 13/13, `fxswitch` fails=0 (12 PASS), `auto` 16/16 спек
(run_live: 21 PASS/0 FAIL — счётчик лог+консоль); живость: breathe
`heroOp 0.302..0.995`, `heroX −109..260`, `cardOp 0.353..0.999`;
breathe-s `barOp 0.505..0.992`, `glintX −84..192`.
- **Код:** WelcomeWizard (мастер 3 шага, ShowStep(int,bool) приватный),
  AdminGate (щит-пульс), `Appearance.ApplyScale(Window, double minScale)`
  перегрузка, `AppSettings.JsonOpts` (AllowNamedFloatingPointLiterals —
  NaN-координаты виджета роняли запись, Save() молчал → мастер
  возвращался каждый запуск). Кодировки: WelcomeWizard/AdminGate.xaml =
  UTF-8 BOM + `\r\r\n` (нормализация `norm_dialogs.py`), их .cs и
  `AppSettings.cs` = без BOM, чистый LF.
- **Git/публикация:** `git init -b main`, первый коммит `f819248` (77
  файлов), push НЕ делался. Байт-точность: `.gitattributes` (`* -text`) +
  repo-local `core.autocrlf=false`/`core.eol=lf`. GitHub: публичное репо
  **pult**, аутентификация — gh CLI (портативный
  `temp/opencode/gh/gh.exe`, БЕЗ GCM; device-flow протухает — логинить
  в момент пуша и код показывать юзеру). Перед пушом — повторный
  `secret_scan.py` (в прошлый раз 81 файл, 0 хитов).
- **Инструменты (temp/opencode):** восстановлены `render_adv` (переключён
  на отдельный каталог рендера `temp/opencode/advdata` — раньше делил
  APPDATA с играми; сдвиг из `dir` убран, префиксы суффиксов отменены —
  схема имён файлов как вручную), 7 аудитов по контрактам HANDOFF §−1/
  BRIEF §6, `run_audits` теперь 8 скриптов (added `audit_newnl`,
  `check_my_xml`) → `audit_report.txt`.
- **ЛОВУШКА рендера (не откатывать):** `render_adv --adv` (свежая копия
  settings) и сидер `games_seed --adv` (кэш игр) — **два разных
  процесса/слоя**: совмещение в одном запуске пропускает reseed
  (каталоги настроек и игр — разные, но только один процесс может
  работать с APPDATA за раз). `EXTRA APPDATA` (игры/кэш) — отдельный
  слой от `advdata` (настроек).
- **`wizshow` (харнесс первых окон):** Show() + DispatcherFrame-насос,
  шаги — рефлексией `ShowStep`, handshake-файл `wizshow_ack.txt`
  («step N» → удалить), запуск ОБЯЗАТЕЛЬНО с `APPDATA=advdata`
  (OnClosing мастера зовёт `TrySave` и пишет settings.json!).
  **Завершение только `Environment.Exit` из тика:** выход через
  `frame.Continue=false` зависал — диспетчер засыпал в WaitForOperation
  и не пробуждался (мастер 35.7с, гейт 1.9–2.4с от пульса); с
  `timer.Stop()` — вообще вечное ожидание. Драйвер `wizshow_run.py`
  (снимки `shot_win.py`, отложка 2с на первый кадр). Скрины уже сняты →
  `docs/wizard-step1..3.png`, `docs/admin-gate.png`.
- **`audit_newnl` (новая истина по NL):** файл обязан быть одностильным
  (CRLF/LF/CRCRLF), BOM опционален. Первая находка: `SimpleHomeView.xaml`
  MIXED (BOM + 135 CRLF + 38 CRCRLF от дизайнера) → нормализован в
  чистый CRLF (BOM сохранён, содержимое нетронуто) — MIXED: 0.
  `norm_dialogs.py` этот файл не покрывает — конфликта нет.
- **Пины BASE (единый источник — `run_renders.py`):** `Effects=3`,
  `Interface=1`, `ExperimentalEnabled=False` — единственный способ
  держать LIGHT и SECTIONS+bars одновременно (замерами).
- **Кэш игр:** `games_seed` → `%APPDATA%\Pult\games.json` = 4 игры с
  SizeBytes (BG3 155ГБ, GoreBox v27b 861МБ, GoreBox27 993МБ, Roblox
  351МБ, total 157566301556); CONTENT-чек games теперь читает
  `render_live-games.png` (21412 пикселей).

### -7. Пакет 0.14.4: Sys B приборная стена + Chat 3 страница-ответ (25.09)
Скетчи одобрены → закрыты; детали в CHANGELOG 0.14.4. Версия
`Pult.csproj` = 0.14.4, запись 0.14.4 в `ReleaseNotes.cs` (`IsCurrent`
— запись ОБЯЗАТЕЛЬНА при бампе).
- **Sys B (SystemView.xaml/.cs):** `BentoPlan()` (покой = ряд 2+2+
  футер; ранг>0 — hero 2 колонки; power/events условные), `BuildBento`
  (пересборка по сигнатуре `ключH/ключC`), `PaintBentoValues` (числа/
  цвета/фразы; диск `pct>=90` → hot-пара в `PaintLeds`), футер
  `BentoNetText/BentoNetBar`, ящик `_drawerKey`. Скрытие HeroRow/чипов/
  LayoutGrid — `ApplyTabChrome` (конец `RefreshTabs`), ТОЛЬКО Обзор.
- **ЛОВУШКА ЭТОЙ ВОЛНЫ (исправлена):** в `BuildBentoTile` забыли
  `tile.Value = val` → `PaintBentoValues` падал NRE на `tt.Value.Text`,
  `catch{}` в `BentoPaint` молча оставлял начальные «–»/«—» (рулетки и
  лед-бар тоже не красились). Правило: рендер живого окна через 6с
  прокачки со значениями «–»/«—» = подозрение на проглоченное
  исключение в `BentoPaint/KpiHeroPaint` — читать код, не гадать.
- **Chat 3 (ChatView.xaml/.cs):** документ 17/1.65, мера 680, цитата с
  2px акцент-линией, поле-сноска 260 (≥1100), `_pendingNotes` (8) →
  сноски, `SetEmpty` репарентит `InputBar` в `EmptySlot` (620), пустой
  экран «Что делаем?» + 4 ссылки. Кодировки не трогать: ChatView.xaml =
  BOM+\r\r\n, оба .cs — LF без BOM; многострочные правки — python.
- **Харнесс — ПЕРЕКАЛИБРОВКА (не откатывать):** `check_014 SECTIONS` —
  точный скан полос (B_Border ±5, w=199..201, пара строк; normal=10 ∈
  [n_xaml−1, n_xaml] — LLM под `ExpOn()`, min=0) вместо «дельты»
  (полосы слипались с краями кнопок: 22/15 → дельта 7 < 9);
  `verify_renders system/палитра-метрик` — violet/pink >50, диск
  `peri+hot >50` (диск 93% → hot), **blue без порога** (трафикозависимая
  заливка футера «Сеть», на стенде 0px) — прежние пороги проверяли
  скрытый Sys B HeroRow.
- **Финальные числа пакета:** Debug/Release/стенд 0/0/0, `check_014`
  11/11, `verify_renders` 15/15, аудиты exit=0, `clip_check` fails=0,
  `fontclick` 13/13, `fxswitch` 12/12 fails=0, `auto` 16/16
  (PASS=16/FAIL=0), живость: breathe `heroOp 0.304..0.949`,
  `heroX −109..260`, `cardOp 0.354..0.997`; breathe-s
  `barOp 0.502..0.993`, `glintX −84..193`.

### -6. Пакет 0.14.3: дизайн-волна — иерархия Системы, тихие полосы, Простой (25.09)
Дизайн-отчёт юзера (пункты 6/7/8) — закрыт, детали в CHANGELOG 0.14.3.
Версия `Pult.csproj` = 0.14.3, запись 0.14.3 в `ReleaseNotes.cs`
(`IsCurrent` — запись ОБЯЗАТЕЛЬНА при бампе).
- **П.6 SystemView («что видно за 2 сек»):** строка чипов
  `ChipTemp/ChipDisk/ChipErr` (цвет = статус, клик по ошибкам →
  `GoErrors_Click` раскрывает «События» и скроллит к ним),
  `KpiCpuSub` → Inlines (частота muted • температура цветом статуса),
  превью Событий «Последняя: … • ещё N» + кнопка «+», диск/P2P —
  метка и ФС в тултипы (`InfoLine(label, value, tip)`).
- **П.7 Блеск Height=2 → статус/тихо:** SystemView — `PaintBlip(status)`
  на KPI, полосы Обзора — `StripLoad/StripPower/StripUpdates/StripEvents`
  + `PaintSectionStrips` (худшая зона); SettingsView — 11 полос тихие
  (`B_Border`), `LlmStrip` = статус проверки (OkStrip #3FB950/ErrStrip
  #F85149), в Minimum — `SetResourceReference("B_Bar")`. **Не ломать:**
  контракт fxswitch `ReferenceEquals(Background, Resources["B_Bar"])`,
  breathe-s (сэмплит первую Height=2/Width=200), `StripWalk()/AnimateBars`
  ищут по геометрии (цвет не важен), Sheens home-card/HeroSheen — не трогать.
- **П.8 Простой:** `RefreshNav` → «Главная»; SimpleHomeView крупнее
  (плитки 200h, иконки 60, кегли 17, Margin 28,24,28,32, число пульса
  56px цветом состояния); дом = **4 плитки-цепочки** (спека PROJECT) —
  `CardChat/CardHistory` перенесены в TextBlock-ссылки под сеткой
  (Visibility в `Reload`: чат=exp, история=всегда), `CardRecycle.Margin`
  фикс (10,10,0,0), `PulseSpark` Collapsed (анимация на скрытом —
  безопасно). Аудит флага Interface: гейты на месте (MainWindow/System/
  Games/Chat/Settings/Dialogs), History — простой осознанно, Widgets —
  без гейта (простые тумблеры).
- **Харнесс — ЛОВУШКИ ЭТОЙ ВОЛНЫ (повторять нельзя):**
  1. стенд держит КОПИЮ `Pult.dll` — после правок Pult пересобрать и
     стенд, ИНАЧЕ рендеры старые (было: render_home с ТРЕМЯ рядами
     плиток, чеки гонялись зря — `home_all.py` нашёл);
  2. живые пробы — только `run_live.py` = `render_adv <view> Effects=3`:
     джобы `main_min`/`home_min` оставляют Effects=1 в advdata и
     fxswitch падает на стартовом чеке («нужен Maximum»); счётчик FAIL —
     по логу стенда `stand_<view>.log` (Mark) + строка `fails=N`
     (Console; render_adv обрезает печать до последних 400 символов);
     НЕ затирать `stand_<view>.log` своим редиректом — стенд пишет его сам;
  3. `clip_check.py` — маска акцента берётся из настроек (юзер сменил
     тему на зелёную `#3FB950`, «Океан»-маска не находила кнопку «Обновить»),
     порог рамки адаптивный `median(prof)+8` (фикс 32 был заточен под «Океан»);
  4. `check_014 SECTIONS` — `strip_groups()`: изолированный прогон
     140..280 px + пара строк y/y+1, дифф>24 к фону на y−4; правило
     «над строкой длинного нет» УБРАНО (рамка карточки над полосой гасила
     детект: normal 19→28, min 17→16, дельта 12);
  5. PowerShell `>` даёт UTF-16 — логи/вывод скриптов читать
     python'ом или `read` в UTF-8.
- **Финальные числа пакета:** Debug/Release/стенд 0/0/0, `check_014`
  11/11, `verify_renders` 15/15, аудиты exit=0, `fontclick` 13/13,
  `fxswitch` fails=0, `auto` fails=0, `clip_check` PASS; живость
  (8с): breathe `heroOp 0.302..0.995`, `heroX −109..260`,
  `cardOp 0.353..0.999`; breathe-s `barOp 0.505..0.991`, `glintX −84..192`.
- **Настройки юзера (факт от 25.09 позднего, НЕ перетирать):** тема
  зелёная — Accent `#3FB950`, Bg `#070B07`, Panel `#0C120C`,
  `Effects=3`, `Interface=1`, `UiScale=0.85`, `FontKey=retro`.

### -5. Пакет 0.14.2: баг-репорт второго ИИ, шрифт/чипы/вердикты (25.09)
«Баг-репорт Pult v0.14.1» (5 пунктов) — закрыт, детали в CHANGELOG 0.14.2.
Версия `Pult.csproj` = 0.14.2, запись 0.14.2 в `ReleaseNotes.cs`
(`IsCurrent` — запись ОБЯЗАТЕЛЬНА при бампе).
- **Шрифт — правило «весь текст через F_Ui»** (пункт 1 репорта, решение
  делегировано ассистенту): `convert_fpixel.py` заменил 111 вхождений в
  14 XAML (`FontFamily="{Static|DynamicResource F_Pixel}"` →
  `{DynamicResource F_Ui}`), бэкап в `fp_backup/`. App.xaml: NavButton-Setter
  тоже Dynamic (Static не обновлялся live), комментарий ресурса переписан.
  В code-behind: `PixelFont()`-кэши удалены (MainWindow/SystemView),
  везде `SetResourceReference(..., "F_Ui")` (live-обновление).
  **Пиксельным остается ДВА места**: определение ресурса App.xaml:49 и
  превью «Аа Привет» чипа «Ретро» SettingsView:565 — не трогать.
- **Чипы масштаба75/80/85** (пункт 2): `add_scale_chips.py` (клон sc90,
  UG 5→4 = 2 ряда), пилюли `Sc75/80/85Pill` в `RefreshScaleChips`.
  A/B: `ab_low.py` → `ab_low_report.txt` + `ab_low_grid.png`
  (Display×Ideal × 0.75/0.80/0.85/1.00, база Display@100).
- **Пункт 3 («полосы не гасятся в Минимуме») — НЕ воспроизводится**:
  цитаты репорта ложные, `BuildCards` внутри `ApplyAppearance` (~575)
  пересобирает карточки на смене Эффектов. Эмпирика — новый режим
  стенда `fxswitch`: `render_adv fxswitch Effects=3` → ждём `fails=0`
  (лог `stand_fxswitch.log`, 14 PASS: 8/8 → 0 → 8/8).
- **Пункт 4 (пороги Auto)**: чистая `AppSettings.DecideEffects`
  (комментарий с формулой), текст порогов в карточке «Авто»
  (SettingsView.xaml:271 + .cs RefreshModeCards), спека — режим стенда
  `auto`: `render_adv auto` → 16/16, `DetectEffects: Maximum` на этой
  машине. Предложения репорта (Tier==1→Normal) НЕ принимать.
- **Пункт 5 (дизайн)** — отложен по решению юзера.
- **Харнесс — смена фактов:** юзер переключил тему на «Океан»
  (#22D3EE/#080E18/#0C1626), Interface=0→**1**, FontKey=bahnschrift→**retro**,
  UiScale=0.9→**0.8** (факт от 25.09, поздний вечер) — маркеры
  `verify_renders`/SECTIONS заточены под базовый фиолет/фон, поэтому
  BASE в `run_renders`/`run_main_variants` пинит теперь И тему, а
  `check_014` читает `BASE_ACCENT`, а не файл юзера. Порог
  `home/заголовок-акцент` 200→60 (заголовок теперь Segoe — тоньше).
  Настройки юзера НЕ перетирать (см. §7 BRIEF).
- **Ретро-волнa (фидбек юзера «с пиксельным текстом надо поработать»,
  скрин: «Случайная» обрезана в «Случай»)** — детали в CHANGELOG 0.14.2:
  - **33 кнопки `Width`→`MinWidth`** (`minwidth_buttons.py`, байт-точно;
    аудит `retro_metrics.py`: у Press Start 2P ровно 1.0em/знак → было
    8 переполнений). Иконки/Binding — не тронуты (5 skipped).
  - **Баг правки B**: подписи чипов sc75/sc80/sc85 были «90%» —
    выправлены (`fix_chip_labels.py`, контроль `verify_chip_labels.py`).
  - **Стенд-фикс**: `Appearance.Init` для всех режимов + ресурс
    `TextElement.FontFamily=F_Ui` на корне headless-вьюхи и `live-`окна
    (прод-наследование не трогали — все окна/диалоги уже несут его на
    RootGrid); до фикса `FontKey=retro`-рендеры молча получали Segoe
    (мерилось: extent 53px вместо 96).
  - **Новый числовой чек `clip_check.py`**: рендер Games ретро → 4 кнопки
    шапки: текст полон и вписан → PASS 0.
  - Спецсимволы `○●■` вне cmap — WPF-fallback, оставлено.
- Порядок контроля как в 0.14.1 (BRIEF §6), плюс `render_adv fxswitch`,
  `render_adv auto` и `clip_check.py` после fontclick/breathe.

### -4. Пакет 0.14.1: живые блики, работающий шрифт, чёткий масштаб (25.09)
Фидбек к 0.14.0 (3 пункта) — закрыт полностью, детали в CHANGELOG 0.14.1.
Версия `Pult.csproj` = 0.14.1, запись 0.14.1 в `ReleaseNotes.cs`
(`IsCurrent` маркирует по InformationalVersion — запись ОБЯЗАТЕЛЬНА при
бампе версии, иначе в «Патчах» не будет «(текущая)»).
- **Блики «статичные» → живые**: `Pult/Services/SheenFx.cs`
  (`Breathe`/`Sweep`/`AnimateBars`), хуки в `MainWindow.
  ApplyAppearance` (hero + карточки по индексу) и в ctor
  `SettingsView`/`WidgetsView` (полосы-разделители). `Sweep` — это
  ДЕТКА-блик (`Child`-Border), полоса НЕ двигается, гоняется только
  `TranslateTransform.X` детки; цикл `Forever` без `AutoReverse` —
  так держать (иначе вернётся «виляние» и упадёт audit_no_wiggle).
  Верификация: `render_adv breathe` / `breathe-s` (сэмплы в
  `stand_breathe*.log`), за 8с heroOp/cardOp/barOp проходят полный
  цикл, X делает wrap −84→199+.
- **Шрифт «не меняется вообще никак»**: баг `Font_Click`
  (`SettingsView.xaml.cs`) — тег чипа `"0".."3"` писался в `FontKey`,
  `ApplyFont` таких ключей не знает. Лечится guard + `Appearance.
  Fonts[...]`; в `AppSettings.Load` есть in-memory миграция
  «0»→retro (файл юзера НЕ трогать, он у юзера от 24.09 с
  FontKey="0"). Верификация: `render_adv fontclick` → 13/13 PASS
  (`stand_fontclick.log`), в конце обязательно смотреть строку
  «файл настроек ЮЗЕРА не изменён».
- **Масштаб «мыло»**: виноват не LayoutTransform, а
  `TextFormattingMode=Display` (grid-fitting в layout-координатах) при
  физическом масштабировании глифа. `Appearance.ApplyScale` пишет
  `TextOptions.TextFormattingMode/TextRenderingMode` на окно И content
  root: `Display` при ≈1, `Ideal` при ≠1 (`Appearance.TextMode`).
  Локальный `TextOptions.TextFormattingMode="Display"` из Views-XAML
  УДАЛЁН (7 файлов) — не возвращать, иначе режим перестанет
  наследоваться от окна. Метрика A/B: lap +38% @0.9, +106% @1.25
  (зоны карточек), при 100% Display чётче Ideal (+12–25%) — поэтому
  гибрид, а не «Ideal всегда». Grayscale-фолбэк ОТМЕНЁН по данным
  (TextRenderingMode в RTB-харнессе не влияет на текст, 0 px
  различий) — не переоткрывать без новых данных.
- **Харнесс (чтобы не искать заново)**: `run_renders.py` и
  `run_main_variants.py` пинят BASE-оверрайды `{'UiScale':'1.0',
  'FontKey':'segoe'}` — без этого рендер уезжает в настройки юзера;
  порядок JOBS важен: `settingsTall_min` ПЕРЕД `settingsTall` (иначе
  затирает `render_settingsTall.png`, который читает check_014);
  `verify_renders.py` читает main-чеки из `render_main_base.png`
  (после run_main_variants `render_main.png` = main_min), чат-полосы
  считают чёрный фон окна фоном (live-рендеры оборачивают вьюху в
  окно поверх чёрного), сэмпл «непустой» — шаг 10px. Стенд:
  `breathe`/`breathe-s` завершаются `HardExit(0)` внутри тика
  (Close внутри DispatcherFrame виснет), `STAND_TFM`/`STAND_TRM`
  переопределяют TextOptions ДО отрисовки (честные рендеры «до»),
  host-путь тоже пишет режим на content root.
- **Порядок контрольных прогонов**: `render_adv fontclick` →
  `breathe` → `breathe-s` → `run_renders.py` → `run_main_variants.py`
  → `check_014.py` → `verify_renders.py` → `run_audits.py`.
  Финальные числа пакета: fontclick 13/13, check_014 11/11,
  verify_renders 15/15, аудиты (pult/reverse/glyphs/xaml/no_wiggle)
  exit=0, сборки Debug/Release/стенд 0/0/0.
- **Ловушки сессии**: PowerShell гасит/ломает кириллицу в stdout —
  логи читать через `read`/python в UTF-8-файлы (напр.
  `smoke_*.txt`), а не печатать в консоль (cp1251 падал на «→»);
  многострочная вставка в python `-c` из PS ломается на кавычках —
  писать скрипт в файл. В `MainWindow.xaml` (BOM+LF) правки —
  однострочные через edit, иначе смешаются окончания строк;
  `Pult.csproj` — BOM+CRLF, `ReleaseNotes.cs` — без BOM/LF.
- **Картинки для ревью юзера**: `report_scale09.png`,
  `report_scale125.png` (ДО=Display сверху / ПОСЛЕ=Ideal снизу, зум
  ×5) — юзер смотрит скрины, показывать маркерами.

### -3. Пакет 0.14.0: статические блики, Система без колец, шрифт/масштаб, плоский Минимум (24.09)
- Версия `Pult.csproj` =0.14.0, запись0.14.0 в `ReleaseNotes.cs`.
  Debug/Release/стенд — все0/0,0 предупреждений. Фидбек юзера закрыт
  по5 пунктам (подробно — CHANGELOG0.14.0): статичные блики вместо
  AutoReverse, Система без колец (сабагент, сессия
  ses_f2c9a00a5ffegsTw7UCF3YBArL), плоский Минимум, ретро-шрифт
  консистентно,4 шрифта + масштаб90–150%, патч-ноуты.
- 🐛 КОРНЕВАЯ ПОЧИНКА СЕССИИ: рендер-оверрайды НЕ работали —
  `Environment.GetFolderPath(SpecialFolder.ApplicationData)` в .NET10
  ИГНОРИРУЕТ переменную `APPDATA`, стенд всегда читал реальные настройки
  юзера (Effects=3/Interface=0 ⇒ «главная» рендерилась как Простой
  интерфейс, flat/шрифт/масштаб молча не применялись, пары
  settingsTall/settingsTall_min были байт-в-байт одинаковы). Фикс —
  `AppSettings.Dir`: сначала `%APPDATA%` напрямую, фолбэк на
  GetFolderPath (штатный запуск не меняется). Верификация до/после:
  `render_adv settingsTall AccentColor=#FF0000` — до: md5==базы,
  лог стенда `accent=#7C6CF0`; после: рендер красный. `settingsTall
  Effects=1` — лог `B_Card=##FF171D2C` (сплошной) + `B_Bar=##007C6CF0`
  (прозрачный) = flat живой. Красный/мин-тесты НЕ трогали файл юзера
  (render_adv пишет только advdata-копию + ассерт после каждого запуска).
- Настройки юзера24.09 (САМ переключал между сессиями, НЕ тронуты):
  Accent #7C6CF0, Bg #0A0E1A, Panel #0E1424, Effects=3 (Maximum),
  Interface=0 (Simple), UiScale=1, FontKey=segoe, ExtraGameDirs list[2].
  NB: для рендеров Interface принудительно1 — из advdata-копии (redirect).
- Рендеры0.14.0 — ВСЕ перегоняны после фикса (прежние шли со сломанным
  redirect): `run_renders.py` (13) + `run_main_variants.py`
  (main_base/scale/bahnschrift/retro/min). Живые варианты шрифта и
  масштаба — ТОЛЬКО через view=main (там `Appearance.Init` в ctor);
  теги home_scale/home_bahnschrift/home_retro идут через live-home без
  Init — для проверок шрифта/масштаба НЕ использовать. Job settingsTall
  из `run_main_variants` УДАЛЁН — он затирал базу
  render_settingsTall.png (так и случилось: оба файла стали Минимумом,
  чинил повторным рендером базы). Первый запуск `live-system` после
  простоя мог зависнуть на120с (таймер прокачки) — повтор проходит;
  при FAIL рендера перезапускать job, не чинить код.
- PIL-контроль `check_014.py` —11/11 PASS: легаси-хексы чисты (17
  рендеров), чип v0.14.0 (372px), семейство Системы1.000, светлый
  (darkest38/dark_cnt4007/median245), масштаб сайдбара83→104,
  шрифты3.3%/4.9% changed, плоский Минимум (сетка12.8→1.2, палитры
  беднее везде), разделители13→4 при XAML=11, content непуст.
  Критерий светлого: на Действиях0 px цвета B_Text — это НОРМА (заголовки
  акцентные, хинты B_Dim); доказ тёмной логики — B_Dim (101,101,119) =
  `Mix(text,panel,.30)` светлой ветки + тёмная статус-строка.
- NB про NL: `App.xaml.cs` фактически CRLF+BOM (старая таблица ниже
  врала) — источник истины `audit_newnl.py`. CRCRLF-файлы: XAML-компилятор
  считает `\r\r\n` двойным переводом — line/pos в ошибках сдвинуты (~×2).

### -2. Пакет 0.13.0: редизайн Системы, цвета из колеса, шов, Снег (24.09)
- Версия `Pult.csproj` = 0.13.0, запись0.13.0 в `ReleaseNotes.cs`
  (окно «Патчи»). Обе сборки Debug/Release —0/0,0 предупреждений.
- Система переподана целиком (сабагент,2-й заход после обрыва связи —
  файлы уже были в целевом состоянии, перепроверены): см. CHANGELOG
  0.13.0. Координатор: интеграция `yv.ApplyPalette()` в
  `MainWindow.ApplyAppearance` + фикс компиляции агента
  `new Thickness(1.5, 0)` → `(1.5, 0, 1.5, 0)` (в WPF нет 2-арг.
  конструктора).
- 🐛 Навигация: в `ShowView` ветка `created == null` (тег actions)
  выходила ранним `return` до `RefreshNavActive()` — прошлый раздел
  держал подсветку. Фикс добавлен перед `SetStatus`.
- Стенд (Temp\opencode\stand): режим `settingsTall` (H=2400 для
  секций ниже сгиба), `light` (ApplyColors in-memory), warning
  `win.FindName` nullable закрыт. Драйвер `render_adv.py view [iface]`
  — APPDATA-redirect копии settings.json с ассерт-сверкой оригинала.
- НЮАНСЫ РЕНДЕРА: standalone-режимы (`settings/home/chat/system/...`)
  рендерят вьюху отдельно — без хрома окна (сайдбар/статус отсутствуют
  по дизайну, md5 детерминирован). Standalone `chat` пуст (FadeIn
  headless) — для чата брать `live-chat` (content=1.00, легаси чисто);
  `settings` без -Tall обрезается на «Вид» — Снег ниже сгиба.
- PIL-скрипты (Temp\opencode): `check_aura2.py` (зазоры слева/справа
  + низ, сглаживание окном7 от dotgrid — шов = устойчивый сдвиг ±8),
  `check_final.py` (легаси-хексы по всем рендерам, чип версии372px,
  гистограмма оттенков Системы, светлый текст: darkest38/dark_cnt4005),
  `check_sneg.py` (swatch144px), `zoom_sys.py` (кропы hero/карточек),
  `check_light.py`/`check_aura.py` (первоначальные). Все проверки
  PASS; аудит (`audit_pult`/`audit_reverse`) — только известные
  ложные [XNAME]/[RESOURCE]/[FONT]-срабатывания.
- Настройки юзера24.09 (САМ переключал, не тронуты, сверялись после
  каждого рендера): Accent #7C6CF0, Bg #0A0E1A, Panel #0E1424,
  Effects=0 (Auto), Interface=1 (Advanced), ExtraGameDirs — list[2].
- Рендеры с финальным кодом (после всех правок и сборок): main
  (Расширенняя через redirect — статус+чип+v0.13.0), live-system
  (новый дизайн с живыми данными), light, settingsTall, live-games,
  live-history, live-home, live-chat — контент полный; просмотр
  инструментом read местами отдаёт stale-картинку — проверять
  повторно или копировать под новым именем.

### -1. Полный аудит пакета0.12.0 (24.09)
- Скрипты аудита лежат в Temp\opencode: `audit_pult.py` (XML/x:Name/
  обработчики/ресурсы/NL), `audit_reverse.py` (обратная сверка — NullRef-
  риск), `audit_glyphs_ctx.py` (глиф против СВОЕГО шрифта), 
  `verify_renders.py` (PIL-маркеры всех слайсов). Ложные срабатывания
  аудита: PART_*-части шаблонов, неиспользуемые x:Name-поля (безвредно),
  `{x:Type` в ресурсах, глиф E3E7 (он из Material Icons, а не MDL2).
- Исправлено: `ReleaseNotes.cs` (окно «Патчи») не содержал записей
  0.11.8/0.11.9/0.12.0 — вписаны; восемь файлов со смешанными окончаниями
  строк нормализованы под свой доминирующий стиль (BOM/содержимое целы,
  assert по NL-юнификации).
- Стенд: `live-*` зависал ПОСЛЕ сохранения PNG — `lw.Close()` и
  `Environment.Exit(0)` из колбэка DispatcherTimer мертвели (loader lock
  нативных DLL). Лечится `HardExit(0)` = TerminateProcess (файлы уже
  синхронно записаны). Добавлен `live-history`: HistoryView грузит
  журнал в `Loaded` — в headless окно не показывается и вьюха пустая
  (в приложении работает); live-рендер даёт12 полос карточек.
- Журнал действий: `%APPDATA%\Pult\actions.json` (Roaming!), логи —
  `%LOCALAPPDATA%\Pult\app.log` (чисто,0 ошибок) и `crash.log` (последняя
  запись22.09, XamlParseException в стиле — историческая, не
  воспроизводится: сборки/рендеры зелёные).
- Настройки юзера после аудита сверены: `#7C6CF0`, Effects=3,
  Interface=1, ExtraGameDirs — не тронуты.

### 0. Пакет 0.12.0: фон, «все соки», Простой vs Минимальный, Игры, Чат, Система
- Жалоба «фон Расширенного сырой/странный» — корень найден: `DotGrid`
  без `Viewbox` (Relative/BoundingBox по умолчанию) растягивал геометрию
  точки на весь тайл 28×28 → сплошная плашка `(33,35,72)`, unique=1.
  Фикс: `Viewbox="0,0 28,28"` Absolute в App.xaml + `MakeDotGrid`, точка
  3×3. Верификация: поле `(10,14,26)` + 900 точек на 280×280 = 3×3/28×28.
- Максимум «все соки»: `AuraGlow` (акцентная аура под контентом, дышит
  6с, только ultra) + сканирующий блик карточек (3.4с) + ховер-подъём
  3px на Transform (без Effect!) + `ApplyAppearance` пересобирает
  карточки — смена режима без рестарта. В Минимуме блик скрыт.
- Простой vs Минимальный: Простой — Segoe SemiBold (24/48/15), плитки
  R16, тёплый вид; Минимум — плоскость. Подписи режимов в SettingsView
  объясняют разницу.
- Игры (`GamesView`): `HeroRow`-чипы статистики (только Расширенный,
  в Простом Collapsed), бар размера — градиент B_Accent → #A5B4FC +
  подпись «X ГБ • N%», чип-источник, верхний акцентный тинт баннера.
- Чат (`ChatView`): полный редизайн шапки/пузыря/ввода — см. CHANGELOG.
- Система (`SystemView`): LED-бары → сплошной градиентный fill
  (ScaleTransform), палитра по метрикам, warn-переход ≥90, плавность
  и свечение только в Максимуме — см. CHANGELOG.
- Стенд: `stand.exe chat|system` для рендер-проверки новых вьюх.
- Правила сохранялись: NL/BOM по файлам (xaml игр/чата CRCRLF+BOM),
  якоря точечные, глифы только существующие, Effect только на элементах
  без текста, настройки юзера не тронуты, обе сборки 0/0 (после
  интеграции), рендер-верификация PIL.

### 1. Живая тема целиком + премиум-контролы (0.11.9)
- Жалоба «при смене темы остаётся свет/синева» — две причины: (а) `ApplyColors`
  перекрашивал 5 кистей из 12 — `B_Border/B_BorderH/B_Tile/B_TileH/B_Sidebar/
  B_Dim/B_Muted/B_Text` оставались дефолтно-синими; (б) кисти в ресурсах
  frozen: при ЗАМЕНЕ ресурса `StaticResource`-захваты в стилях и старых окнах
  навсегда держат старый объект.
- `ThemeService.ApplyColors`: полная палитра производных (border = Mix(panel,
  accent, .16), borderH = .34, tile/tileH/sidebar/dim/muted/text) + новые
  ресурсы: `B_Card` (вертикальный градиент глубины карточек), `B_Bar`
  (акцентный блик, гаснет вправо), `DotGrid` (сетка фона: была хардкод-синей
  плашкой `#151C33` — остаточная синева всей контентной области; теперь
  Mix(panel, accent, .18)). Мутация кистей на месте, frozen → замена объекта.
- 348 `StaticResource B_*` → `DynamicResource B_*` во всех XAML: смена темы
  живьём догоняет и открытые окна, и стили `App.xaml` (иначе ховер/активные
  триггеры стилей обновиться не могли бы).
- `App.xaml`: TextBox — собственный `ControlTemplate` (системный хром с белой
  подложкой исключён навсегда; ховер/фокус — акцент, фокус ещё и `B_Soft`),
  `ActionCard`/`SimpleActionCard` на `B_Card`; шаблон `NavButton` — Grid с
  `TemplateBinding Background` (иначе пропадает hover) + рельс `NavInd`.
- `MainWindow.xaml.cs.RefreshNavActive`: активный — `SetResourceReference(B_Soft)`
  и `NavInd.Opacity=1` через `Template.FindName` (после `ApplyTemplate`),
  неактивный — `ClearValue` (локальное значение перебивает триггер стиля).
- `SimpleHomeView`: карточка «Что я сделал» (глиф `E81C` — существующий глиф
  Истории, не угадан) + делегат `GoHistory` → `ShowView("history")`;
  видимость вместе с чатом — сетка 4 карточки без чата / 6 с чатом, без дырок.
- Живой смене темы — три правки `MainWindow`: `OnThemeChanged` зовёт
  `BuildCards()` (с `CardsGrid.Children.Clear()` — иначе дубли карточек) и
  `RefreshNavActive()` (иконки и активный пункт держат ОБЪЕКТЫ кистей,
  без перечитывания — старый акцент); `ApplyAppearance` больше не кэширует
  `_actionsBg` (поле удалено) — `SetResourceReference(B_Bg/DotGrid)`.
  Остальные вьюхи перезаряжаются при показе (Reload/Refresh в `ShowView`);
  ChatView не нуждается: статика на XAML (DynamicResource), пузыри
  строятся по факту сообщения — свежие кисти.
- Стенд получил режимы `themetest` и `live` — ВАЖНО для верификации:
  ResourceDictionary держит кисти замороженными (дамп: и исходные, и
  записанные — frozen=True), смена темы ВСЕГДА подменяет объект, обновление
  идёт рефрешем DynamicResource-выражений, а БЕЗ прокачки диспетчера
  выражения не обновляются. Поэтому `themetest` (без pump) показывает
  «старые» рельс/кольца — это АРТЕФАКТ стенда, не бага приложения.
  Истина — режим `live`: Show() + DispatcherFrame + DispatcherTimer
  (1-й тик — `ApplyColors("#FF5555",...)`, 2-й — RenderTargetBitmap →
  Close), прокачка работает; `Run` сознательно не используется — он бы
  стрельнул App.OnStartup (гейт/мастер). Пиксели live: рельс (255,85,85),
  кольца — 0 зелёных/464 красных, блики карточек — 0 зелёных.
  `ApplyColors` публичный и НЕ пишет настройки — им и переключать
  (настройки юзера после всех тестов == бэкапу, hash сверен).
- Верификация: стенд-рендер (headless; `--pump` из стенда УДАЛЁН: без окна
  `Run`/`Invoke(ApplicationIdle)` виснут, `PushFrame` тоже — а с показанным
  окном `PushFrame`+таймеры работают, см. режим `live`) + PIL-пробы.
  ВНИМАНИЕ следующему ИИ: `tools.read` для PNG/JPEG в этой сессии отдавал
  ПЕРЕПУТАННЫЙ кэш — рендеры проверять пикселями (`getpixel`/гистограмма),
  глазами — только после сверки. Обе сборки 0/0.

### 2. Персонализация доделана + чистка мёртвого (0.11.8)
- Аудит показал: `ThemeService.Apply/ApplyPreset/ApplyColors` не звал НИКТО,
  UI пресетов не было, поля `AccentColor/BackgroundColor/PanelColor` в настройках
  ни на что не влияли. `HueShift`/`CurrentAccent`/`Proc.RunText` — мёртвые.
- `App.OnStartup`: `ThemeService.Apply()` до первых окон (тема — сразу, без мигания).
- `ThemeService`: событие `Changed` (Invoke в конце `ApplyColors`) + `ApplyCustom`
  (нормализует hex, сохраняет в настройки, применяет).
- `SettingsView.xaml`: карточка «Персонализация» между «Вид» и «Экспериментальное».
  Глиф `E790` (палитра) — отрендерен полосой и проверен картинкой, НЕ угадан.
  4 пресета-кнопки со свотчами 14×14, 3 HEX-поля, «Применить», `ThemeStatus`.
- `SettingsView.xaml.cs`: `RefreshThemeCards()` зовётся из `Reload()`; выбранность
  пресета = сравнение трёх нормализованных полей; `Theme_Click`/`ThemeApply_Click`
  пишут статус в `ThemeStatus` (ошибка — строкой, как принято).
- `MainWindow.OnThemeChanged`: обнуляет `_heroGlow/_heroGlowStatic/_ringGlow1..3`
  и зовёт `ApplyAppearance()`. Подписка в конструкторе, ОТПИСКА в `Closed`
  (static event держит объект — не забыть, иначе утечка при пересоздании окна).
- `SimpleHomeView.ApplyPulseGlow`: страж `_pulseGlowAccent` — свечение пересобирается
  при смене акцента. Причина: дом кэшируется в `_views["simplehome"]` и НЕ
  пересоздаётся при показе (только `Reload()`), подписаться на событие нельзя.
- Мёртвое удалено: `Proc.RunText`, `C_AccentH`. Логи `bin/Debug/.../crash.log`
  и `trace.log` (остатки старых сборок) удалены.
- Проверено: Debug и Release 0/0; `xaml_audit` — 0 битых ключей, 0 потерянных хендлеров.

### 3. Дёргание SystemView — чинено, без версии (`Pult/Views/SystemView.xaml.cs`)
- Причина: тик таймера (2–15 с) делал полный перестрой панелей + `ApplyLayout(true)` + FadeIn.
- `RefreshFast()`: тик обновляет только метры in-place (бары CPU/RAM, 3 текста, сеть).
  Полный перестрой — по кнопке и не чаще раза в минуту (`_lastFull`).
- `ApplyLayout(force, animate)`: цели колонок считаются отдельно (`target[3]`),
  ходы без изменений пропускаются (`SameOrder`), анимация только со вкладки
  (`Tab_Click` → `animate: true`).
- Перестроение панелей — в одном проходе (`Dispatcher.DisableProcessing`).
- Проверено стендом: метры живые, раскладка стабильна, путь с анимацией не падает.

### 4. Релиз 0.11.1 (патч, без -beta)
- `Pult.csproj`: `0.11.0-beta` → `0.11.1`. «О программе» показывает
  `InformationalVersion` (`SettingsView.xaml.cs`).
- `Services/ReleaseNotes.cs`: заметки человеческим языком + `Current()`/`IsCurrent()`/`Format()`.
- `Pult/PatchesDialog.xaml(.cs)`: отдельное окно-таймлайн (полоса+пиксель,
  пилюля «ТЕКУЩАЯ», копирование). Кнопка «Патчи» в «О программе».
- Кнопка «Папка данных» переехала из «О программе» в «Диагностику»
  (пара к «Открыть историю изменений»). Решение: пользователям она нужна, но там.
- Пиксель-ретро унифицирован: Простой дом, названия игр (`GamesView.xaml.cs`,
  `F_Pixel 11`), кольца (герой 14, виджет 11), часы виджета 36 + дата 10
  (двоеточие/кириллица в шрифте проверены через fontTools cmap),
  бейджи, бренд, опции настроек/мастера, навигация (`NavButton` 11),
  итог дисков. Тело/кнопки/чат-контент/логи — Segoe/Consolas сознательно.
- Чат: подпись «ВЫ ■» у своих пузырей, статус модели в шапке
  (`HeaderStatusText`), +2 шутки в `Tips` (всего 7).
- Бары Системы: блик включённых сегментов + видимая лесенка, всё в одном `PaintLeds`.
- Виджет: `WS_EX_TOOLWINDOW` (вне Alt+Tab; из таскбара и так убран),
  крестик ОСТАВЛЕН (нужен). Логика/таймеры не тронуты.
- Бонусы: честный футер хоткеев (`ActionsHint`: 1…7/1…8), Esc закрывает `ResultDialog`.

### 5. Релиз 0.11.0-beta: место на дисках + глубокая очистка
- Плитка «Место на дисках» (`MainWindow.RunAction`): отчёт + строка корзины +
  кнопка «Освободить» (Temp + корзина, журнал). Без новой плитки.
- `DiskCleanupService`: `DismAnalyze`/`DismCleanup` (RU+EN разбор, сторож 5/30 мин),
  `UpdateCacheInfo`/`Clean` (bounce wuauserv/bits), общие `FormatSize`/`Plural`
  (дубли из `SimpleHomeView` удалены).
- Кнопка «Глубокая очистка…» — футер «Папок системы», только за флагом;
  без админа — честный отказ. `Windows.old` сознательно не трогаем (права).
- Проверено: `UpdateCacheInfo` намерил 12/~100 МБ; парсинг DISM сверен на образцах RU+EN.

### 6. Корзина в Простом + версия 0.10.0-beta
- `RecycleBinInfo()` (`SHQueryRecycleBin`, `(0,0)` при ошибке) + живая карточка
  `CardRecycle` (подпись, plural, адаптивный МБ/ГБ) + строка и чистка в «Проверить всё».
- Иконки сверены рендером шрифта в PNG: урна `E74D` → Корзина, ластик `E75C` → Мусор.
- Маржа карточки переключается в коде (ряд с/без чата).

### 7. Чат спрятан без флага (кнопка, плитка, `LlmCard`, `CardChat` + `ApplyAppearance`
в `ExpBox`-хендлерах). Важно: юзер дважды смотрел на СТАРЫЙ бинарь
(было 3 сборки разных дат) — всегда проверять, что запущено свежее.

### 8. Починка падения Простого дома
- `SimpleActionCard`: `CornerRadius` через `x:Static double` не конвертируется
  в Setter → `Value="10"`. Единственный `x:Static` в XAML был там.

### 9. GamesView в Простом
- Скрыты Размеры/+Папка/сорт-по-размеру/источник/бары; `SizesGap`/`AddGap`/`EmptyHint`
  именованы; `ApplySimpleMode()` в конструкторе и `Render()`.

### 10. Общие эффекты + Пульс (начало сессии)
- `Services/EffectsHelper.cs` (общий `FadeIn`), `PulseGlowFrame`, приватный
  `FadeIn` и мёртвое поле `_mode` из `MainWindow` удалены.
- `RenderPulse`: `ComputeAsync()` + `AppendToHistory()` (двойной `Task.Run` убран).

## Грабли и правила (выстрадано)

- Ряд XAML лежит с окончаниями `\r\r\n` — многострочные правки через edit-тул
  НЕ ложатся; только однострочные якоря без отступов либо Python-скрипт
  с явным UTF-8 (PowerShell ломает кириллицу — табу проекта).
- Кириллица/`«»`/эм-дэш в oldString — только если побайтово уверен; иначе якорь ASCII.
- Private-use глифы (`\uE81C` и т.п.) в исходниках — только через Python с `\u`-эскейпами.
- Глифы MDL2 НЕ гадать: рендерить полосу скриптом `glyphs` (был в tmp) и смотреть картинкой.
- Проверка: временный headless-стенд (STA, `Pult.App` + `InitializeComponent()`,
  инстанциирование вьюх; для асинхронного UI — качать `DispatcherFrame`;
  окна НЕ показывать — стенд выполняется на машине юзера). Рендер в PNG через
  `RenderTargetBitmap` — так проверялись чат/патчи/виджет/полоса глифов.
- `Dispatcher.Invoke` без крутящегося цикла вешает пул-потоки — в стендах ок,
  т.к. завершаем через `Environment.Exit`.
- АНОМАЛИЯ (открыта): стенд `sparktest` (SimpleHome + `DispatcherFrame` 8с)
  трижды вис внутри `PushFrame` — таймер выхода не срабатывал. Тот же паттерн
  в 6 других стендах работал. Повтор `pring2` (тот же фрейм) — тоже вис;
  мгновенный стенд без фрейма — ок. Подозрение: связка вложенного фрейма с
  бесконечными часами `PulseBeat` в этом конкретном сценарии; живое приложение
  (свой цикл `Application.Run`) вису не подвержено — таймеры там тикают
  нормально. Перед повторением такого стенда — разобраться.
- ОТКАТ: кольцо Пульса в Простом убрано по требованию — юзер просил только
  чинить полоску, кольцо было самодеятельностью. Вернули число 40 + полоса
  120×36 снизу; `PulseRing`-ссылки вычищены полностью (проверено grep).
  Урок: «такой же как в расширенном» читать буквально только после уточнения.
- РЕНДЕР-АРТЕФАКТ: чёрный кадр в стенде = `FadeIn` ставит Opacity=0, а часы
  без живого цикла не тикают. Лечится рендером в Минимуме (там сразу 1).
  Это НЕ баг приложения.
- 0.11.2: полоса — ЭКГ из 12 ритмов (параметрический билд P/QRS/T + ротация
  2.8с только в Максимуме), дыхание только на цифрах.
- Временные проекты — только в `%TEMP%\opencode`, удалять после. Настройки юзера
  (`%AppData%\Pult\settings.json`) для тестов бэкапить и возвращать.
- `StaticResource` падает только в рантайме — сборка зелёная ничего не значит для XAML.
- Не трогать: `Pulse`-скоринг, откаты журнала, `win-x64` паблиш от 21.09 (неизвестно чем собран).
- Юзер смотрит скрины и гоняет Debug; новое показывать маркерами
  («ластик вместо урны») — иначе снова будет смотреть старый бинарь.
