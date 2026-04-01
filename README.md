# EggDesk

**Cross-platform remote desktop manager** built with .NET 10 and AvaloniaUI by [Eggspot](https://github.com/eggspot).

Manage RDP, SSH, and VNC connections from a single app -- with an encrypted local vault, optional GitHub sync or any Git remote with username/password, and a clean dark UI that runs natively on Windows, macOS, and Linux.

---

## Features

- **RDP, SSH, VNC** -- native backends per platform (AxMSTscLib on Windows, FreeRDP on macOS/Linux, SSH.NET terminal)
- **Encrypted vault** -- AES-256-GCM with Argon2id key derivation. Two modes:
  - **Local mode** -- password-only, no account required, works offline forever
  - **GitHub OAuth or any Git remote with username/password** -- vault synced as a private Git repo across your devices
- **Tabbed sessions** -- tab switch < 50 ms; sessions stay alive in background
- **Global search** -- fuzzy-match across all connections (Ctrl+K)
- **Group & tag connections** -- organize by environment, protocol, or team
- **Import connections** -- drag in `.rdp`, `.rdm`, `.ini` (MobaXterm), or mRemoteNG XML files
- **Dark / Light / System theme**
- **NativeAOT on Windows** -- single binary, no .NET runtime required

---

## Vault Modes

| | Local Mode | Git Sync |
|---|---|---|
| Account required | None | GitHub or any Git remote |
| Encrypted at rest | Yes (AES-256-GCM) | Yes (AES-256-GCM) |
| Sync across devices | No | Yes (private Git repo) |
| Works offline | Yes | Yes (cached locally) |
| First run | Set a master password | Sign in via Device Flow |

In **local mode** the vault lives at `~/.config/spotdesk/vault.json` and is unlocked only with your master password. Nothing leaves your machine.

---

## Getting Started

### Download

Grab the latest release for your platform from the [Releases](../../releases) page.

| Platform | Format |
|---|---|
| Windows | Single `.exe` (NativeAOT) |
| macOS | `.app` bundle (ReadyToRun) |
| Linux | `.AppImage` |

### Build from source

```bash
# Prerequisites: .NET 10 SDK

git clone https://github.com/eggspot/EggDesk.git
cd EggDesk

# Restore packages
dotnet restore SpotDesk.sln

# Run (development)
dotnet run --project src/SpotDesk.App

# Run tests
dotnet test SpotDesk.sln

# Publish -- single-file executable (any platform)
./scripts/publish.sh              # auto-detect platform
./scripts/publish.sh win-x64      # or specify explicitly
./scripts/publish.sh osx-arm64
./scripts/publish.sh linux-x64

# Or manually:
dotnet publish src/SpotDesk.App -r win-x64 -c Release
```

### Single-file delivery

EggDesk publishes as a **single executable** -- no installer, no runtime, no external dependencies (except FreeRDP on Linux/macOS for RDP support).

| Property | Value |
|---|---|
| `PublishSingleFile` | All managed code bundled into one binary |
| `SelfContained` | .NET runtime embedded -- no install needed |
| `PublishTrimmed` | Unused code removed (~30% smaller) |
| `EnableCompressionInSingleFile` | Binary is compressed |
| `IncludeNativeLibrariesForSelfExtract` | Native libs (LibGit2Sharp's libgit2) bundled inside, extracted to temp on first launch |

The Git sync engine uses **LibGit2Sharp** (embedded native `libgit2`), so users don't need git installed.

### Linux runtime dependencies

```bash
# For RDP support (FreeRDP):
sudo apt install libfreerdp3 libssh2-1 libvncserver-dev libice6 libsm6 libfontconfig1
```

---

## Tech Stack

| Layer | Technology |
|---|---|
| UI | AvaloniaUI 11 |
| Language | C# 13 / .NET 10 |
| ViewModels | CommunityToolkit.Mvvm (source-gen, AOT-safe) |
| Encryption | AES-256-GCM + Argon2id (Konscious) |
| RDP (Windows) | AxMSTscLib COM interop |
| RDP (macOS/Linux) | FreeRDP 3.x (P/Invoke) |
| SSH | SSH.NET + custom VT100 renderer |
| VNC | RemoteViewing |
| Git sync | LibGit2Sharp |
| Tests | xUnit + NSubstitute + FluentAssertions + Avalonia.Headless |

---

## Project Structure

```
src/
  SpotDesk.Core/        # Domain: vault, crypto, auth, sync, import
  SpotDesk.Protocols/   # RDP / SSH / VNC session backends
  SpotDesk.UI/          # AvaloniaUI views, controls, view-models
  SpotDesk.App/         # Entry point, DI bootstrap, platform init
tests/
  SpotDesk.Core.Tests/
  SpotDesk.UI.Tests/
  SpotDesk.Protocols.Tests/
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for detailed module descriptions and dependency graphs.

---

## Testing

EggDesk has **~195 automated tests** organized by milestone.

### Test categories

| Category | What it covers | Count |
|---|---|---|
| M1 | Vault, crypto, OAuth, keychain, key derivation | ~95 |
| M2 | Git sync, conflict resolution | ~10 |
| M3 | File importers (RDP, RDM, mRemoteNG, MobaXterm) | ~13 |
| M4 | ViewModels, headless UI dialogs | ~50 |
| M5 | Integration: SettingsVM flows, SessionTab lifecycle, view selector | ~30 |
| Protocols | Terminal buffer, VT100 parser | ~5 |

### Running tests

```bash
# All tests
dotnet test SpotDesk.sln

# Single milestone
dotnet test SpotDesk.sln --filter "Category=M1"

# Self-healing test loop (build once, retry up to 5x)
./scripts/test-loop.sh                    # all tests
./scripts/test-loop.sh --milestone M1     # single milestone
.\scripts\test-loop.ps1 -Milestone M5     # PowerShell on Windows
```

### CI

GitHub Actions runs all tests on **Linux, Windows, macOS** on every push and PR.

---

## Contributing

All contributions are welcome -- bug fixes, features, documentation, translations.

1. Fork the repo and create a branch (`git checkout -b feat/my-feature`)
2. Make your changes and add tests
3. Run `dotnet test SpotDesk.sln` -- all tests must pass
4. Open a pull request

Code you contribute is licensed MIT.

---

## Security

Credentials are **never stored in plaintext**. The vault file (`vault.json`) contains only AES-256-GCM ciphertext and is safe to commit to a private Git repo.

What never leaves your device unencrypted:
- Passwords and SSH keys
- OAuth tokens
- The derived master key

If you find a security issue, please open a **private** GitHub Security Advisory rather than a public issue.

---

## Sponsoring

EggDesk is free and open source (MIT). If it saves you time, consider sponsoring development:

- Click the **Sponsor** button at the top of this page (GitHub Sponsors)

Your support funds ongoing development, new platform support, and security audits.

---

## License

[MIT](LICENSE)
