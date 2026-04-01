# EggDesk by Eggspot -- Project Blueprint
> Remote access, at Eggspot speed. | .NET 10 / AvaloniaUI 11 / C# 13

---

## Table of Contents
1. [Project Overview](#1-project-overview)
2. [Platform Support Matrix](#2-platform-support-matrix)
3. [Architecture](#3-architecture)
4. [Module Status](#4-module-status)
5. [UI/UX Design System](#5-uiux-design-system)
6. [Layout & Navigation](#6-layout--navigation)
7. [Screen-by-Screen Wireframes](#7-screen-by-screen-wireframes)
8. [Auth & Vault Key Design](#8-auth--vault-key-design)
9. [Data Flow](#9-data-flow)
10. [Tech Stack](#10-tech-stack)
11. [Testing](#11-testing)
12. [Outstanding Work & Roadmap](#12-outstanding-work--roadmap)
13. [Milestone Prompts](#13-milestone-prompts)

---

## 1. Project Overview

EggDesk (codebase: SpotDesk) is a cross-platform remote desktop manager built by Eggspot Company Limited. It manages RDP, SSH, and VNC connections from a single tabbed UI with an encrypted vault, optional Git-based sync, and native performance on Windows, macOS, and Linux.

**Core differentiators:**
- **No master password by default** -- vault key derived from GitHub OAuth identity + device fingerprint
- **Single-file delivery** -- one executable per platform, no installer required
- **Sub-50ms tab switching** -- sessions stay alive in memory, framebuffer reattach only
- **NativeAOT on Windows** -- no .NET runtime install needed

**Current state:** Early development. Core domain logic (vault, crypto, auth, sync, import) is substantially implemented and well-tested (~195 tests). Protocol backends are partial (SSH ~70%, FreeRDP ~40%, Windows RDP stub, VNC minimal). UI shell is functional with good MVVM coverage, but session views need work.

---

## 2. Platform Support Matrix

| Platform       | RDP Backend            | SSH | VNC | Binary Format | Status |
|----------------|------------------------|-----|-----|---------------|--------|
| Windows 10/11  | AxMSTscLib (COM)       | Yes | Yes | NativeAOT .exe | RDP: Stub |
| macOS 13+      | FreeRDP 3.x (P/Invoke) | Yes | Yes | ReadyToRun     | RDP: Partial |
| Linux (X11)    | FreeRDP 3.x (P/Invoke) | Yes | Yes | AppImage       | RDP: Partial |
| Linux (Wayland)| FreeRDP 3.x (P/Invoke) | Yes | Yes | AppImage       | RDP: Partial |

**Linux dependencies (apt):**
```
libfreerdp3 libssh2-1 libvncserver-dev libice6 libsm6 libfontconfig1
```

Linux and macOS share `FreeRdpBackend.cs`. The `IRdpBackend` interface abstracts platform differences.

---

## 3. Architecture

### 3.1 Solution Structure

```
SpotDesk.sln
+----- src/
|      +-- SpotDesk.Core/           # Domain: vault, crypto, auth, sync, import
|      |   +-- Auth/                # OAuth (GitHub Device Flow + PAT), Raw Git credentials
|      |   +-- Crypto/             # AES-256-GCM, Argon2id KDF, device fingerprint
|      |   +-- Import/             # .rdp, .rdm, mRemoteNG, MobaXterm importers
|      |   +-- Models/             # ConnectionEntry, CredentialEntry, ConnectionGroup
|      |   +-- Sync/              # LibGit2Sharp git sync, conflict resolver
|      |   +-- Vault/             # VaultService orchestrator, JSON model, session lock
|      |
|      +-- SpotDesk.Protocols/     # RDP / SSH / VNC session backends
|      |   +-- FreeRdp/           # P/Invoke for macOS + Linux
|      |   +-- Windows/           # AxMSTscLib COM (stub)
|      |   +-- Ssh/               # SSH.NET + VT100 terminal
|      |   +-- Vnc/               # RemoteViewing (minimal)
|      |   +-- SessionManager.cs  # ConcurrentDictionary session pool
|      |
|      +-- SpotDesk.UI/           # AvaloniaUI views, controls, view-models
|      |   +-- Controls/          # ConnectionListItem, SessionTab, SearchBox, StatusBar
|      |   +-- Converters/        # Protocol badges, latency colors, theme helpers
|      |   +-- Dialogs/           # OAuth, NewConnection, ImportWizard, VaultUnlock
|      |   +-- Styles/            # ColorTokens, DarkTheme, Controls
|      |   +-- ViewModels/        # MVVM with CommunityToolkit.Mvvm source-gen
|      |   +-- Views/             # MainWindow, RdpView, SshView, VncView, etc.
|      |
|      +-- SpotDesk.App/          # Entry point, DI bootstrap, platform init
|
+----- tests/
       +-- SpotDesk.Core.Tests/
       +-- SpotDesk.Protocols.Tests/
       +-- SpotDesk.UI.Tests/
```

### 3.2 Dependency Graph

```
                    SpotDesk.App
                   /     |      \
                  /      |       \
          SpotDesk.UI    |    SpotDesk.Protocols
              |    \     |       /
              |     \    |      /
              +------SpotDesk.Core
```

**Layer rules:**
- **Core** -- zero UI/Protocol references. Pure domain.
- **Protocols** -- depends on Core only (for models). No UI.
- **UI** -- depends on Core + Protocols. All ViewModels AOT-safe.
- **App** -- references all three. Wires DI.

### 3.3 DI Bootstrap (Program.cs)

```csharp
var services = new ServiceCollection()
    // Crypto & Identity
    .AddSingleton<IDeviceIdService, DeviceIdService>()
    .AddSingleton<IKeyDerivationService, KeyDerivationService>()
    .AddSingleton<IKeychainService>(sp => KeychainServiceFactory.Create())
    // Auth
    .AddSingleton<OAuthClientConfig>()
    .AddSingleton<IOAuthService>(sp => new OAuthService(...))
    // Vault
    .AddSingleton<ISessionLockService, SessionLockService>()
    .AddSingleton<IVaultService>(sp => new VaultService(...))
    // Sync
    .AddSingleton<IGitSyncService>(sp => new GitSyncService(...))
    // Protocols (platform-conditional)
    .AddSingleton<IRdpBackend>(sp =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsRdpBackend()
            : new FreeRdpBackend())
    .AddSingleton<ISessionManager>(sp => new SessionManager(...))
    // ViewModels
    .AddSingleton<MainWindowViewModel>(sp => new MainWindowViewModel(...))
    .AddSingleton<SettingsViewModel>(sp => new SettingsViewModel(...))
    ...
    .BuildServiceProvider();
```

---

## 4. Module Status

### SpotDesk.Core

| Module | Status | Implementation Notes |
|--------|--------|---------------------|
| VaultCrypto | **Implemented** | AES-256-GCM, random nonce, 16-byte tag. Clean and correct. |
| KeyDerivation | **Implemented** | Argon2id (3 iter, 64MB, 4 parallel). Device key + password derivation. |
| DeviceIdService | **Implemented** | Linux: /etc/machine-id, macOS: IOKit serial, Windows: registry. SHA-256 hashed. |
| VaultService | **Implemented** | Unlock (GitHub + local modes), CRUD entries, device management, vault migration. |
| VaultModel | **Implemented** | JSON source-gen. VaultFile, DeviceEnvelope, VaultEntry records. |
| SessionLockService | **Implemented** | GCHandle pinned memory. Lock/unlock/dispose. |
| OAuthService | **Implemented** | GitHub Device Flow + PAT. PKCE loopback. 24h identity cache. |
| KeychainService | **Implemented** | Windows CredWrite, macOS SecKeychain, Linux libsecret/file fallback. |
| MasterPasswordFallback | **Implemented** | Same Argon2id with user-supplied password + random salt. |
| GitSyncService | **Implemented** | LibGit2Sharp clone/pull/push. Offline queue with backoff. |
| ConflictResolver | **Implemented** | Last-write-wins by updatedAt. Decrypts both sides, merges, re-encrypts. |
| RdpFileImporter | **Implemented** | .rdp key:type:value parser. Host, port, resolution extraction. |
| DevolutionsImporter | **Implemented** | .rdm XML and JSON. Protocol mapping, group hierarchy. Encrypted files: NotImplemented. |
| MRemoteNgImporter | **Implemented** | mRemoteNG confCons.xml parser. Protocol mapping, group nesting. |
| MobaXtermImporter | **Implemented** | MobaXterm .ini parser. SSH/Telnet/RDP/VNC section parsing. |
| OAuthClientConfig | **Implemented** | Env var overrides, bundled GitHub client ID. |
| RawGitCredentialService | **Implemented** | Username/password credential storage for any Git HTTPS remote. |

### SpotDesk.Protocols

| Module | Status | Implementation Notes |
|--------|--------|---------------------|
| SessionManager | **Implemented** | ConcurrentDictionary<Guid, IRdpSession> + SSH. TCP pre-warm. Has race condition in GetOrAdd. |
| IRdpBackend/IRdpSession | **Implemented** | Clean interfaces. Missing IAsyncDisposable. |
| WindowsRdpBackend | **Stub** | All methods no-ops. No COM interop. |
| FreeRdpBackend | **Partial (40%)** | Connects, allocates WriteableBitmap. No event loop, no frames. P/Invoke errors. |
| FreeRdpNative | **Partial** | Declarations present. Wrong entry points. LibName property is dead code. |
| SshSession | **Partial (70%)** | Connect/auth/shell works. Resize broken. SSH agent TODO. |
| SshSessionManager | **Implemented** | ConcurrentDictionary pool. Same GetOrAdd race as SessionManager. |
| Vt100Parser | **Partial (65%)** | CSI sequences, 256-color, truecolor SGR. No UTF-8, no OSC/DCS. |
| TerminalBuffer | **Partial** | Char/attr grid, scrollback. Resize doesn't reallocate. Concurrency issues. |
| VncSessionManager | **Minimal (20%)** | Connect/store only. No framebuffer, no input, not integrated. |

### SpotDesk.UI

| Module | Status | Implementation Notes |
|--------|--------|---------------------|
| MainWindow | **Functional** | Custom title bar, sidebar+tabs+session pane, keyboard shortcuts. |
| SidebarView | **Functional** | Connection tree, groups, inline search, context menus. |
| ConnectionTreeViewModel | **Functional** | Groups, search, fuzzy match, FrozenDictionary index. LINQ in filter (hot path). |
| SessionTabViewModel | **Functional** | Status tracking, auto-reconnect timer, proper IDisposable. |
| SearchViewModel | **Functional** | Ctrl+K overlay, fuzzy match, quick-connect. |
| SettingsView/VM | **Mostly Functional** | All tabs render. SSH Keys placeholder. Revoke button unwired. |
| NewConnectionDialog/VM | **Functional** | Protocol toggle, port auto-update, compiled bindings. |
| ImportWizard/VM | **Functional** | 3-step wizard, drag-drop, preview. Vault persist uses Task.Delay placeholder. |
| OAuthConnectDialog | **Functional** | Device Flow + PAT. Poor MVVM (all code-behind). |
| RdpView | **Functional** | Framebuffer rendering, toolbar, input forwarding. |
| SshView | **Placeholder** | Renders dots instead of glyphs. Timer + buffer wired up. |
| VncView | **Partial** | Framebuffer works. Toolbar buttons unwired. |
| VaultUnlockDialog | **Non-functional** | No handlers, no bindings. |
| WelcomeView | **Functional** | Branding, actions, recent connections, sign-in prompt. |
| ThemeService | **Functional** | Dark/Light/System. DynamicResource throughout. |
| SessionViewSelector | **Functional** | IDataTemplate, no reflection. Does NOT cache views (recreates on switch). |

### SpotDesk.App

| Module | Status | Notes |
|--------|--------|-------|
| Program.cs | **Functional** | Full DI registration. Platform-conditional RDP. |
| PlatformBootstrap | **Dead code** | Initialize() never called from Main(). |

---

## 5. UI/UX Design System

### 5.1 Color Tokens

```xml
<!-- Backgrounds -->
<Color x:Key="BgBase">#0F1117</Color>
<Color x:Key="BgSurface">#171B26</Color>
<Color x:Key="BgElevated">#1E2333</Color>
<Color x:Key="BgHover">#252A3D</Color>
<Color x:Key="BgActive">#2D3350</Color>

<!-- Accent (Eggspot brand) -->
<Color x:Key="AccentPrimary">#3B82F6</Color>
<Color x:Key="AccentSubtle">#1E3A5F</Color>
<Color x:Key="AccentText">#93C5FD</Color>

<!-- Status -->
<Color x:Key="StatusConnected">#22C55E</Color>
<Color x:Key="StatusConnecting">#F59E0B</Color>
<Color x:Key="StatusError">#EF4444</Color>
<Color x:Key="StatusIdle">#6B7280</Color>

<!-- Text -->
<Color x:Key="TextPrimary">#E8EAF0</Color>
<Color x:Key="TextSecondary">#9AA3B8</Color>
<Color x:Key="TextTertiary">#5A6478</Color>
```

### 5.2 Typography
- Display: 22px/500 -- window title, section headers
- Heading: 16px/500 -- panel titles, dialog headers
- Body: 14px/400 -- connection names, labels
- Small: 12px/400 -- metadata, timestamps, badges
- Mono: 13px/400 -- hostnames, IPs, terminal (JetBrains Mono)

### 5.3 Motion
All transitions: 120ms ease-out. Never animate what the user is actively controlling.

### 5.4 Spacing Scale
4px / 8px / 12px / 16px / 24px / 32px

---

## 6. Layout & Navigation

### 6.1 Main Window
```
+-----------------------------------------------------------------------+
| TITLEBAR (32px) -- custom chromeless, drag region                     |
| [=] EggDesk    [Search Ctrl+K]              [Sync ^] [Settings] [-X] |
+----------+------------------------------------------------------------+
|          | TAB BAR (40px)                                             |
| SIDEBAR  | [+ New] [* prod-web-01 x] [* db-server x] [...]           |
| (240px)  +------------------------------------------------------------+
|          |                                                            |
| Groups   |                  SESSION PANE                              |
| + My     |          (RDP / SSH / VNC renders here)                    |
|   Servers|          WelcomeView when no session open                  |
|   > Prod |                                                            |
|   > Dev  |                                                            |
|          |                                                            |
| Recents  |                                                            |
|  web-01  |                                                            |
|  db-01   |                                                            |
| [+ Add]  |                                                            |
+----------+--------------------------------------------[StatusBar]-----+
```

### 6.2 Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+K | Global search overlay |
| Ctrl+N | New connection |
| Ctrl+W | Close active tab |
| Ctrl+\ | Toggle sidebar |
| Ctrl+1-9 | Switch to tab N |
| Ctrl+Shift+F | Toggle fullscreen session |
| Ctrl+Alt+Break | Release keyboard focus from RDP |
| Ctrl+R | Reconnect active session |
| Ctrl+, | Open settings |

---

## 7. Screen-by-Screen Wireframes

See [docs/ui-sketches.html](docs/ui-sketches.html) for full ASCII wireframes of all views.

---

## 8. Auth & Vault Key Design

### 8.1 Philosophy -- No Master Password by Default

EggDesk eliminates the master password for OAuth users. The vault key is derived from the user's stable OAuth identity + device fingerprint. After one-time browser auth, every launch unlocks silently in <100ms.

### 8.2 Key Derivation Chain

```
GitHub userId (long)  +  deviceId (SHA-256 of machine data)
        |
        v
  Argon2id KDF  (iterations=3, memory=65536, parallelism=4)
  salt = fixed app constant (non-secret, domain separation)
        |
        v
  deviceKey (32 bytes)  -- unique per user+device pair
        |
        v
  AES-256-GCM decrypt DeviceEnvelope.encryptedMasterKey
        |
        v
  masterKey (32 bytes)  -- encrypts all vault entries
        |
        v
  AES-256-GCM decrypt each VaultEntry.ciphertext
        |
        v
  plaintext credential JSON
```

### 8.3 Vault File Structure (`vault.json`)

```json
{
  "version": 2,
  "kdf": "argon2id:3:65536:4",
  "salt": null,
  "mode": "github",
  "devices": [
    {
      "deviceId": "a3f8c2...",
      "deviceName": "MacBook Pro (work)",
      "encryptedMasterKey": "<base64>",
      "iv": "<base64 12-byte>",
      "addedAt": "2026-03-01T08:00:00Z"
    }
  ],
  "entries": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "iv": "<base64 12-byte>",
      "ciphertext": "<base64 AES-256-GCM>"
    }
  ]
}
```

**Git-safe:** Everything in vault.json is ciphertext. OAuth tokens and derived keys never touch Git.

### 8.4 Vault Modes

| Mode | Key Source | First Run | Unlock |
|------|-----------|-----------|--------|
| `github` | OAuth userId + deviceId | Device Flow in browser | Silent (<100ms) |
| `local` | Master password + random salt | Set password dialog | Password prompt |

Migration between modes is supported via `VaultService.MigrateLocalToGitHubAsync()`.

### 8.5 OS Keychain Storage

| Platform | Backend | Key |
|----------|---------|-----|
| Windows | Credential Manager (CredWrite) | `spotdesk:oauth:github` |
| macOS | Keychain Access (SecKeychain) | `spotdesk:oauth:github` |
| Linux (GNOME) | libsecret (D-Bus) | `spotdesk:oauth:github` |
| Linux (fallback) | AES-encrypted file `~/.config/spotdesk/keystore` | -- |

### 8.6 Device Onboarding

1. New device: no keychain token -> OAuthConnectDialog shown
2. User authenticates with same GitHub account -> same userId
3. Different deviceId -> no matching DeviceEnvelope
4. Trusted device approves: decrypts masterKey, re-encrypts with new deviceKey, pushes new envelope
5. New device pulls, finds its envelope, unlocks

---

## 9. Data Flow

### 9.1 Vault Unlock (GitHub Mode)
```
App Launch
  -> Keychain.Retrieve("spotdesk:oauth:github")
  -> (if null) Show OAuthConnectDialog -> Device Flow -> Store token
  -> OAuthService.GetCachedIdentityAsync() -> userId
  -> KeyDerivation.DeriveDeviceKey(userId, deviceId)
  -> Load vault.json -> Find DeviceEnvelope
  -> VaultCrypto.DecryptMasterKey(envelope, deviceKey)
  -> SessionLockService.SetMasterKey(masterKey)
  -> Vault Unlocked
```

### 9.2 Connection Lifecycle
```
User activates connection
  -> MainWindowViewModel.OpenTab(entry)
  -> SessionTabViewModel.ConnectAsync()
  -> SessionManager.GetOrAdd(entry.Id) -> create/reuse session
  -> session.ConnectAsync(entry, credential)
  -> SessionViewSelector.Build() -> RdpView / SshView / VncView
  -> FrameUpdated events -> WriteableBitmap rendering
```

### 9.3 Git Sync
```
Vault mutation
  -> VaultService.SaveVaultAsync()
  -> GitSyncService.SyncAsync()
  -> LibGit2Sharp: Stage -> Commit("spotdesk: sync [timestamp]") -> Push
  -> (offline) Queue + exponential backoff retry
```

---

## 10. Tech Stack

| Component | Package | Purpose |
|-----------|---------|---------|
| UI framework | Avalonia 11, Avalonia.X11 | Cross-platform XAML |
| ViewModels | CommunityToolkit.Mvvm | Source-gen, AOT-safe MVVM |
| Encryption | System.Security.Cryptography | AES-256-GCM |
| KDF | Konscious.Security.Cryptography.Argon2 | Argon2id key derivation |
| SSH | SSH.NET | SSH transport |
| Git sync | LibGit2Sharp | Embedded libgit2 |
| VNC | RemoteViewing | VNC client |
| Tests | xUnit, NSubstitute, FluentAssertions | Testing |
| Headless UI tests | Avalonia.Headless.XUnit | ViewModel/view testing |

---

## 11. Testing

### 11.1 Test Organization

| Category | Scope | Tests | Quality |
|----------|-------|-------|---------|
| M1 | Vault, crypto, OAuth, keychain | ~95 | Excellent |
| M2 | Git sync, conflict resolution | ~10 | Good |
| M3 | File importers | ~13 | Good |
| M4 | ViewModels, headless UI | ~50 | Good |
| M5 | Integration flows | ~30 | Good |
| Protocols | Terminal buffer | ~5 | Basic |
| **Total** | | **~195** | |

### 11.2 Test Helpers
- `VaultFixture` -- fresh in-memory vault per test
- `FastKeyDerivationService` -- low-iteration Argon2id for speed
- `InMemoryKeychainService` -- no OS keychain dependency
- `MockHttpMessageHandler` -- queued HTTP response stubs
- `FakeIdentity` -- fake GitHub identity for auth tests

### 11.3 Known Test Gaps
- No SSH/RDP/VNC backend tests
- No performance benchmarks (despite <50ms tab switch target)
- No concurrency tests for SessionManager
- No SearchViewModel or LocalPrefsService tests
- Duplicate test files without category traits (legacy)
- `PlatformBootstrap` is dead code with no tests

---

## 12. Outstanding Work & Roadmap

### P0 -- Critical (blocks usable product)

1. **Fix FreeRDP P/Invoke** -- wrong entry points, dead LibName property, missing event loop
2. **Implement Windows RDP backend** -- currently all no-ops
3. **Fix SSH terminal rendering** -- need actual glyph rendering, not single-pixel dots
4. **Fix SSH terminal resize** -- use SSH channel request, not ANSI escape
5. **Wire VaultUnlockDialog** -- currently non-functional
6. **Call PlatformBootstrap.Initialize()** from Program.Main()

### P1 -- High (security & correctness)

7. **Atomic vault file writes** -- write to .tmp then rename
8. **Zero master key copies** -- stop calling .ToArray() on pinned key
9. **Fix SessionManager.GetOrAdd race** -- use Lazy<T> pattern
10. **Fix blocking async in SessionManager.Close()** -- use async disposal
11. **Implement macOS keychain Delete** -- currently silent no-op
12. **Thread-safe GitSyncService queue** -- replace Queue with ConcurrentQueue
14. **Wire ConflictResolver into GitSyncService** -- handle non-fast-forward

### P2 -- Medium (quality & UX)

15. **Cache views in SessionViewSelector** -- avoid recreating on tab switch
16. **Fix memory leaks** -- unsubscribe event handlers in UI
17. **Refactor OAuthConnectDialog to ViewModel** -- testability
18. **Remove LINQ from connection tree filter** -- hot path per blueprint
19. **Cache brush allocations in converters**
20. **Wire SettingsView Revoke button** to RevokeDeviceCommand
21. **Add UTF-8 decoding to Vt100Parser**
22. **Fix TerminalBuffer resize** -- reallocate backing array

### P3 -- Low (polish)

23. Pin package versions (remove wildcards)
24. Remove duplicate legacy test files
25. Add performance benchmarks
26. Add SSH/VNC protocol tests
27. Implement SSH agent auth
28. Complete VNC integration with main SessionManager
29. Fix TerminalBuffer DeleteChars/InsertChars bounds
30. Persist fallback device ID to disk

---

## 13. Milestone Prompts

These prompts are designed for AI-assisted "vibe coding". Copy each into a session and iterate.

### Milestone 1 -- Vault + Crypto + Auth (COMPLETE)
Core domain: vault model, AES-256-GCM, Argon2id KDF, OAuth Device Flow, keychain, session lock.

### Milestone 2 -- Git Sync (COMPLETE)
LibGit2Sharp clone/pull/push, conflict resolver, offline queue.

### Milestone 3 -- File Importers (COMPLETE)
RDP, Devolutions RDM, mRemoteNG, MobaXterm importers.

### Milestone 4 -- AvaloniaUI Shell (COMPLETE)
MainWindow, sidebar, tabs, theme system, keyboard shortcuts.

### Milestone 5 -- SSH Terminal (PARTIAL)
SSH.NET session, VT100 parser (implemented), terminal rendering (placeholder).

### Milestone 6 -- RDP Windows (STUB)
AxMSTscLib COM interop. Not started.

### Milestone 7 -- RDP macOS/Linux (PARTIAL)
FreeRDP P/Invoke. Structure present, critical bugs in native interop.

### Milestone 8 -- Connection Tree + Search (COMPLETE)
ConnectionTreeViewModel, SearchViewModel, fuzzy match, quick-connect.

### Milestone 9 -- Tab Session Manager (COMPLETE)
SessionManager, SessionTabViewModel, auto-reconnect, TCP pre-warm.

### Milestone 10 -- Settings + Sync UI (MOSTLY COMPLETE)
SettingsView with all tabs. SSH Keys placeholder, Revoke button unwired.

### Milestone 11 -- Import Wizard UI (COMPLETE)
3-step wizard, drag-drop, preview, progress. Vault persist uses placeholder delay.

---

*EggDesk Blueprint v3.0 -- Eggspot Company Limited, Ho Chi Minh City, Vietnam*
*Built with .NET 10 / AvaloniaUI 11 / C# 13*
*Auth: OAuth-derived vault key / No master password required*
