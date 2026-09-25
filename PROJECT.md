# Pult — карта проекта для ИИ

Десктоп-помощник Windows (WPF, `net10.0-windows`, C# 12, Nullable on).
Всё локально (`%AppData%\Pult`, `%LocalAppData%\Pult`), наружу — только запросы к LLM.
Точка входа: `Pult/App.xaml(.cs)` (гейт админа `AdminGate` → мастер `WelcomeWizard` → `MainWindow`).

## Экраны (`Pult/Views/`)
| View | Что это |
|---|---|
| `SimpleHomeView` | Главная Простого интерфейса: светофор, «Проверить всё», 4 кнопки-цепочки |
| `MainWindow` Действия | 8 плиток мгновенных действий + hero с кольцами CPU/RAM/Диск |
| `SystemView` | 21 плашка в 6 вкладках (Обзор/Скорость/Хранилище/Твики/Железо/Сеть), автоколонки по высоте |
| `GamesView` | Steam + ярлыки + папки, поиск/сорт/«Случайная», запуск |
| `ChatView` | Чат с LLM: пузыри, markdown-лайт, инструменты (tools), история `history.json` |
| `SettingsView` | Модель, Вид (Эффекты+Интерфейс), Персонализация (пресеты+свои цвета), Экспериментальное, Автопорядок, папки, диагностика/бэкап |
| `HistoryView` | Журнал действий с откатом + точка восстановления |
| `WidgetsView` | Управление виджетом (`WidgetWindow`: часы+CPU/RAM поверх окон) |

## Сервисы (`Pult/Services/`, все static, все методы try/catch → строки)
`SystemMonitor` (железо/диски/сеть/процессы/автозагрузка/программы),
`DiskCleanupService` (дубликаты SHA-256, Temp, тяжёлые, корзина),
`GameService` (скан Steam/ярлыков/папок, маркеры движков incl. `*_Data` для Unity),
`TweakService` (реестр-твики: чтение `GetState`→"on/off/na", запись `Apply`→"Готово."/ошибка),
`PrivacyService` (приватность, схемы питания, часы, P2P),
`ExtraInfoService` (Wi-Fi/TCP/аудио/обновления/ошибки), `SensorsService` (LibreHardwareMonitor),
`WingetService`, `AppxService`, `LlmClient` (инструменты `BuildTools()`/`ToolCount()`,
read-only список `ReadOnlyTools`, остальное — с подтверждением),
`AppSettings` (JSON, оси `Effects`/`Interface`, миграция старых ключей через `JsonDocument`),
`ActionJournal` (`actions.json`, откат твик/приватность/питание/автозагрузка; удаления — только запись),
`AppLog`/`CrashLog`,
`ThemeService` (4 пресета + свои HEX; `Apply()` в `App.OnStartup` до первых окон,
полная палитра производных цветов + ресурсы `B_Card`/`B_Bar`/`DotGrid`,
событие `Changed` → окна пересобирают свечения под акцент),
`DialogFx` (появление окон), `RestorePointService`, `Proc` (запуск процессов с таймаутом),
`FileService`, `Game`/`DiskInfo` — рекорды рядом с сервисами (`Game` — в `GameService.cs`,
`DiskInfo` — в `SystemMonitor.cs`),
`WebService` (поиск/чтение ссылок — инструменты LLM), `HardwareService` (обзор железа),
`GameArt` (обложки/иконки игр), `PulseService` (скоринг пульса, ЭКГ-полоса),
`EffectsHelper` (общие `FadeIn`/`PulseBeat`/`PulseGlowFrame`), `DesignTokens` (углы),
`ReleaseNotes` + окно `PatchesDialog` (таймлайн патчей).

## Оси и гейты
- `Effects`: Auto(детект: `RenderCapability.Tier`, `ClientAreaAnimation`, батарея)/Minimum/Normal/Maximum.
  Читается через `AppSettings.EffectiveEffects()`, НЕ напрямую из поля.
- `Interface`: Simple (дом вместо Действий, нет Системы в меню, техполя скрыты) / Advanced.
- `AppSettings.ExpOn()` = флаг + Advanced. Опасные кнопки/чат/удаления — только через него.
- `StrictPrivacyMode`: режет `get_clipboard` из tools + отказ в выполнении, без `history.json`, стирает историю+лог при выходе.

## Паттерны UI
- Стиль: тёмный ретро, квадрат, бордер 2px, акцент/фон/панель — **живые** `DynamicResource`
  (`B_Accent/B_Bg/B_Panel/B_Soft/B_Tile`), остальное `StaticResource`.
- Действие пользователя: `ConfirmDialog.Ask()` → `Task.Run` → `Dispatcher.Invoke` →
  `ResultDialog` (+ `ActionJournal` для изменений).
- Результат сервиса — строка: `"Готово..."` успех, `"Ошибка..."` провал. Никогда null.
- `TweakService.Apply` после записи перепроверяет `GetState`; `RunProcess` бросает при `ExitCode != 0`.

## Табу (горький опыт)
- НЕ править файлы через PowerShell (ломает UTF-8 кириллицу) — только точные правки.
- `DropShadowEffect` на элементе с текстом = мыло (растр). Свечение — только на рамках/кольцах без текста; один экземпляр эффекта на один элемент.
- `Thickness`/`Segments`/`Fill` у `Ring` — DependencyProperty; `Value=100` рисуется кругом, clamp в coerce.
- `ComboBox` кастомный (SelectionBox — явный TextBlock, иначе чёрный текст).
- Медленные проверки (winget/pnputil/appx) — сторожа 30с, опоздавшие данные дорисовываются.
- Кэш игр: `GetGames(false)` отдаёт кэш; кнопка «Обновить» = `force=true`.
