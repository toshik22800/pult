# Pult

[![build](https://github.com/toshik22800/pult/actions/workflows/build.yml/badge.svg)](https://github.com/toshik22800/pult/actions/workflows/build.yml) · **English** · [Русский](README.ru.md)

**A desktop control panel for Windows: system monitor, privacy tweaks, local-LLM chat, game library, and an action journal with one-click undo. Everything runs on your machine.**

Dark "instrument-panel" UI, always-on-top widget, first-run setup wizard.
WPF · C# · .NET 10 · open source.

![Main screen](docs/render_main_base.png)
![Settings](docs/render_settings.png)
![Games](docs/render_live-games.png)
![Light theme](docs/render_light.png)

## Features

- **Actions** — instant action tiles plus a hero gauge with CPU / RAM / disk rings.
- **Home** — traffic-light status, "Check everything", one-button chains.
- **System** — 21 widgets across six tabs: overview, speed, storage, registry tweaks, hardware, network. Columns adapt to window height.
- **Games** — scans Steam, shortcuts and folders; search, sort, "Random pick", covers, launch.
- **Chat** — talks to your local LLM (Ollama by default), markdown-lite, tools gated by confirmation, history in `history.json`.
- **History** — every action is journaled with rollback and restore points.
- **Widget** — clock and system load on top of other windows.
- **Settings** — model & endpoint, effects (Auto/Low/Normal/Max), UI density, 4 theme presets + custom HEX colors, scale, game folders, diagnostics, backup.

## Requirements

- Windows 10 / 11, x64.
- **To run the release:** nothing else — `Pult.exe` is self-contained, no .NET install needed.
- **To build from source:** [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Download

1. Get `pult-*.zip` from [Releases](https://github.com/toshik22800/pult/releases/latest).
2. Unpack it and start `Pult.exe`.
3. Optional integrity check — the SHA-256 of every asset is published in the release notes:

```powershell
Get-FileHash .\pult-0.14.5-win-x64.zip -Algorithm SHA256
```

## ⚠️ Before you run

- The app **asks for administrator rights** — required for some tweaks.
- Some features **change Windows registry values and privacy settings**.
- Anything that touches the system goes through an explicit confirmation dialog and lands in an undoable journal — but **create a restore point / backup first** if you are not sure.
- **Open source** — you can read the code before running it.

## What it does NOT do

- **No telemetry, no analytics, no trackers, no auto-updates** — there is simply no code for it.
- **Network activity: none by default.** The app never phones home on its own. It touches the network only when *you* explicitly ask:
  - the LLM endpoint — default `http://127.0.0.1:11434` (local Ollama); any external address is entered by you in Settings;
  - web search and reading links — only on your request from the chat.
- Your settings, history and journal stay in `%APPDATA%\Pult` on your disk.

## This is the original project

Official releases and development happen **only** at [`toshik22800/pult`](https://github.com/toshik22800/pult). Copies and forks may exist — the original lives here, and this is where issues, discussions and updates happen.

## First run

On the first launch you get an admin-rights gate and a three-step setup wizard (interface, effects, quick start):

![Wizard step 1](docs/wizard-step1.png)
![Wizard step 2](docs/wizard-step2.png)
![Wizard step 3](docs/wizard-step3.png)
![Admin gate](docs/admin-gate.png)

## Build from source

```powershell
git clone https://github.com/toshik22800/pult.git
cd pult
dotnet build Pult/Pult.csproj -c Release
```

Run from the working folder:

```powershell
dotnet run --project Pult/Pult.csproj -c Release
```

Self-contained single-file release — one `Pult.exe`, no .NET required on the target machine (this is the exact command CI runs):

```powershell
dotnet publish Pult/Pult.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/win-x64
```

`publish/win-x64/Pult.exe` is zipped and attached to a GitHub Release.

## Project layout

```
Pult/
  App.xaml(.cs)          entry point: admin gate → wizard → main window
  MainWindow.xaml(.cs)   main window (Actions, tabs, menu)
  WelcomeWizard.*        first-run wizard (3 steps)
  AdminGate.*            admin-rights gate → relaunch elevated
  Views/                 SimpleHomeView, SystemView, GamesView, ChatView,
                         SettingsView, HistoryView, WidgetsView
  Services/              SystemMonitor, TweakService, PrivacyService,
                         GameService, LlmClient, WebService, ThemeService,
                         AppSettings, ActionJournal, AppLog… (all static)
  Fonts/                 embedded .ttf
PROJECT.md               project map for developers / AI assistants
BRIEF.md                 quality regimen and verification steps
CHANGELOG.md             version history
CONTRIBUTING.md          how to build and contribute
```

## Quality

Every development wave ends with the same honest pipeline (`BRIEF.md` §6): Debug + Release builds → renders of all screens against pixel thresholds → XAML audits (formatting, no wiggle animations, MDL2 glyph codes) → live scenario checks with markers, not eyeballing. Dialog XAML is kept byte-exact (UTF-8 BOM + `\r\r\n`), enforced by automated normalization.

`CHANGELOG.md` and the in-app Patches window (`ReleaseNotes`) update with every release; the version in `Pult.csproj` and `IsCurrent` in `ReleaseNotes` must match.

## Feedback

- Bugs and concrete suggestions → [Issues](https://github.com/toshik22800/pult/issues)
- Impressions, pros & cons, ideas → [Discussions](https://github.com/toshik22800/pult/discussions)
- Code contributions welcome — see [CONTRIBUTING.md](CONTRIBUTING.md)

## License

[MIT](LICENSE) © 2026 toshik22800 — free to use, modify and redistribute, including commercially, as long as the copyright notice and the license travel with the code.
