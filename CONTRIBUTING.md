# Contributing to Pult

Thanks for helping out — bug reports, ideas and code are all welcome.

## Build

- [.NET 10 SDK](https://dotnet.microsoft.com/download), Windows 10/11 x64.

```powershell
git clone https://github.com/toshik22800/pult.git
cd pult
dotnet build Pult/Pult.csproj -c Debug
dotnet build Pult/Pult.csproj -c Release
```

CI ([`.github/workflows/build.yml`](.github/workflows/build.yml)) runs the same
builds on every push and pull request and attaches a single-file artifact.

## Before you open a PR

Code changes go through the project's quality pipeline (`BRIEF.md` §6):

1. `dotnet build` for **both** Debug and Release — zero errors.
2. Render screenshots of all screens against pixel thresholds (`verify_renders`).
3. XAML audits — formatting, no wiggle animations, MDL2 glyph codes.
4. Live scenario checks with markers — honest numbers, not eyeballing.

Docs/markdown-only changes need none of the above — a green CI run is enough.

## Code conventions

- **UI strings are Russian.** README is bilingual: `README.md` (English, primary)
  and `README.ru.md` (Russian mirror) — keep both in sync.
- Services are `static` classes; views live in `Views/`. Keep the View/Service
  split described in `PROJECT.md`.
- Keep file encodings and line endings exactly as they are:
  - `WelcomeWizard.xaml` / `AdminGate.xaml` — UTF-8 BOM + `\r\r\n` (as emitted by
    the designer); their `.cs` files and `AppSettings.cs` — no BOM, plain LF;
  - for everything else — don't introduce mixed line endings.
- **No telemetry and no unprompted network calls. Ever.** Network is used only
  for features the user explicitly invokes.
- Don't change the dark theme, sidebar navigation or the `ThemeService` palette
  architecture (4 presets + custom HEX) without discussing it first.

## Commits are signed

Commits here carry a `Verified` badge (SSH signature). To sign yours:

```powershell
ssh-keygen -t ed25519 -C "your-email@example.com"
gh ssh-key add ~/.ssh/id_ed25519.pub --type signing
git config --global gpg.format ssh
git config --global user.signingkey ~/.ssh/id_ed25519.pub
git config --global commit.gpgsign true
```

## Pull requests

1. Fork → branch → PR against `main`.
2. Describe *what* changed and *why*; attach screenshots for UI changes.
3. Link related Issues.

## No code? Still contribute

Use [Issues](https://github.com/toshik22800/pult/issues) for bugs and
[Discussions](https://github.com/toshik22800/pult/discussions) for everything
else — feedback is what keeps the project alive.
