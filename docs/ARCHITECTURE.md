# EggDesk Architecture

> .NET 10 / AvaloniaUI 11 / C# 13 -- Eggspot Company Limited

---

## Dependency Graph

```
SpotDesk.App
  |-- SpotDesk.UI
  |     |-- SpotDesk.Core
  |     |-- SpotDesk.Protocols
  |           |-- SpotDesk.Core
  |-- SpotDesk.Core
  |-- SpotDesk.Protocols
```

**Layer rules:**
- `SpotDesk.Core` has zero references to UI or Protocols. Pure domain logic.
- `SpotDesk.Protocols` depends on Core (for `ConnectionEntry`, `CredentialEntry`). No UI references.
- `SpotDesk.UI` depends on Core + Protocols. All ViewModels use CommunityToolkit.Mvvm source-gen.
- `SpotDesk.App` wires DI, bootstraps platform services, references all three.

---

## Module Breakdown

### SpotDesk.Core (Domain Logic)

| Subsystem | Status | Key Types | Description |
|---|---|---|---|
| **Crypto** | Implemented | `VaultCrypto`, `KeyDerivation`, `DeviceIdService` | AES-256-GCM encrypt/decrypt, Argon2id KDF, platform device fingerprinting |
| **Vault** | Implemented | `VaultService`, `VaultModel`, `SessionLockService` | Vault orchestrator (unlock, CRUD entries, device management), JSON model with source-gen, pinned master key in memory |
| **Auth** | Implemented | `OAuthService`, `KeychainService`, `OAuthClientConfig`, `MasterPasswordFallback` | GitHub Device Flow + PAT, Bitbucket App Passwords, OS keychain abstraction (Win/Mac/Linux), master password fallback mode |
| **Sync** | Implemented | `GitSyncService`, `ConflictResolver` | LibGit2Sharp clone/pull/push, last-write-wins conflict resolution by `updatedAt` |
| **Import** | Implemented | `RdpFileImporter`, `DevolutionsImporter`, `MRemoteNgImporter`, `MobaXtermImporter` | Import from .rdp, .rdm (Devolutions XML/JSON), mRemoteNG XML, MobaXterm .ini |
| **Models** | Implemented | `ConnectionEntry`, `CredentialEntry`, `ConnectionGroup`, `SessionState` | Domain records. JSON source-gen via `VaultJsonContext` |

### SpotDesk.Protocols (Session Backends)

| Subsystem | Status | Key Types | Description |
|---|---|---|---|
| **Session Manager** | Implemented | `SessionManager`, `ISessionManager` | `ConcurrentDictionary<Guid, IRdpSession>` + SSH sessions, TCP pre-warm on hover |
| **RDP (Windows)** | Stub | `WindowsRdpBackend` | All methods are no-ops. AxMSTscLib COM interop not yet implemented |
| **RDP (FreeRDP)** | Partial (~40%) | `FreeRdpBackend`, `FreeRdpNative` | P/Invoke declarations present but several entry points are incorrect. No event loop for frame updates |
| **SSH** | Partial (~70%) | `SshSession`, `SshSessionManager` | Connect/disconnect/auth works. Terminal resize is broken (writes escape instead of SSH channel request). SSH agent auth is TODO |
| **Terminal** | Partial (~65%) | `Vt100Parser`, `TerminalBuffer` | Comprehensive VT100/xterm CSI support with 256-color and truecolor. Missing UTF-8 multi-byte, OSC sequences. Buffer has concurrency issues |
| **VNC** | Minimal (~20%) | `VncSessionManager` | Can connect via RemoteViewing. No framebuffer rendering, no input, not integrated with main SessionManager |

### SpotDesk.UI (AvaloniaUI Frontend)

| Subsystem | Status | Key Types | Description |
|---|---|---|---|
| **Main Shell** | Functional | `MainWindow`, `MainWindowViewModel` | Custom title bar, sidebar + tab bar + session pane layout, keyboard shortcuts |
| **Sidebar** | Functional | `SidebarView`, `ConnectionTreeViewModel` | Connection tree with groups, inline search, drag-to-reorder, context menus |
| **Session Tabs** | Functional | `SessionTab`, `SessionTabViewModel` | Tab lifecycle, auto-reconnect timer, status tracking, disposable |
| **Search** | Functional | `SearchBox`, `SearchViewModel` | Ctrl+K overlay, fuzzy match, quick-connect by hostname |
| **Theme** | Functional | `ThemeService`, `ColorTokens.axaml`, `DarkTheme.axaml` | Dark/Light/System, DynamicResource throughout, 120ms transitions |
| **Settings** | Mostly Functional | `SettingsView`, `SettingsViewModel` | General, Appearance, Vault & Sync, Trusted Devices, Shortcuts, About. SSH Keys tab is placeholder |
| **New Connection** | Functional | `NewConnectionDialog`, `NewConnectionDialogViewModel` | Protocol toggle, port auto-update, credential builder, group assignment |
| **Import Wizard** | Functional | `ImportWizard`, `ImportWizardViewModel` | 3-step wizard with file picker, preview, progress. Actual vault persist uses placeholder delay |
| **OAuth Dialog** | Functional (poor MVVM) | `OAuthConnectDialog` | Device flow + PAT auth for GitHub/Bitbucket. All logic in code-behind |
| **RDP View** | Functional | `RdpView` | Framebuffer rendering, auto-hide toolbar, keyboard/mouse forwarding |
| **SSH View** | Placeholder | `SshView` | Terminal renders single pixels instead of glyphs. 60fps render timer wired up |
| **VNC View** | Partial | `VncView` | Framebuffer display works, toolbar buttons unwired |
| **Vault Unlock** | Non-functional | `VaultUnlockDialog` | No button handlers, no bindings. Pure placeholder |
| **Welcome** | Functional | `WelcomeView` | Shown when no sessions open, recent connections, quick tips |
| **Converters** | Implemented | 6 converter classes | Singleton instances where possible. Some create brushes per call (perf concern) |
| **Controls** | Implemented | `ConnectionGroupItem`, `ConnectionListItem`, `ProtocolBadge`, `StatusBar` | Recursive group template, status dot colors match spec |

### SpotDesk.App (Entry Point)

| Subsystem | Status | Description |
|---|---|---|
| **DI Bootstrap** | Implemented | All major services registered. Platform-conditional RDP backend selection |
| **PlatformBootstrap** | Dead Code | `Initialize()` defined but never called from `Program.Main()` |

---

## Key Design Decisions

### 1. No Master Password by Default
The vault key is derived from `Argon2id(githubUserId + deviceId)`. Users authenticate once per device via GitHub/Bitbucket OAuth. Every subsequent launch unlocks silently in <100ms by reading the cached token from the OS keychain.

### 2. NativeAOT Compatibility
All JSON serialization uses `System.Text.Json` source generators (`JsonSerializerContext`). No reflection anywhere. `CommunityToolkit.Mvvm` source-gen for ViewModels. `IDataTemplate` implemented directly (no reflection-based `DataTemplate`).

### 3. Single-File Delivery
`PublishSingleFile` + `SelfContained` + `PublishTrimmed`. LibGit2Sharp's native `libgit2` is bundled via `IncludeNativeLibrariesForSelfExtract`. No external `git` CLI required.

### 4. Session Reattach on Tab Switch
`SessionManager` keeps live sessions in a `ConcurrentDictionary`. Tab switching reattaches the existing framebuffer/shell stream -- never reconnects. Target: <50ms.

### 5. Vault File is Git-Safe
`vault.json` contains only AES-256-GCM ciphertext. Device IDs are SHA-256 hashes (non-sensitive). OAuth tokens and derived keys never touch Git.

### 6. Theme System
All color tokens defined in `ColorTokens.axaml`. Theme variants in `DarkTheme.axaml` using `ResourceDictionary.ThemeDictionaries`. All XAML uses `DynamicResource` for live switching.

---

## Known Limitations

### Critical
- **FreeRDP P/Invoke library resolution is broken** -- `LibName` property is dead code; `LibraryImport` hardcodes `"libfreerdp3"` which won't resolve on Linux (`libfreerdp3.so.3`)
- **FreeRDP `freerdp_input_get` has wrong entry point** -- mapped to `freerdp_get_sub_system`, will crash at runtime
- **No FreeRDP event loop** -- `freerdp_check_fds` not called, frames never arrive
- **SSH terminal resize writes ANSI escape instead of SSH channel request** -- and the byte write has length=0
- **SessionViewSelector recreates views on every tab switch** -- violates <50ms target

### High
- **Master key copies not zeroed** -- `VaultService` calls `.ToArray()` on the pinned key repeatedly, defeating pinned-memory protection
- **Vault file writes are not atomic** -- crash during write corrupts the file
- **`SessionManager.GetOrAdd` race** -- can create duplicate sessions under contention
- **Blocking async in `SessionManager.Close()`** -- `GetAwaiter().GetResult()` can deadlock on UI thread
- **macOS keychain Delete is a no-op** -- revoked tokens persist
- **`GitSyncService._pendingPaths` is not thread-safe** -- `Queue<string>` without synchronization

### Medium
- **Bitbucket user-info fetch missing auth header** -- likely broken at runtime
- **`ConflictResolver` never wired into `GitSyncService`** -- fast-forward failures are unrecoverable
- **`VaultUnlockDialog` is non-functional** -- no button handlers
- **Memory leaks in UI event subscriptions** -- `RdpView`, `VncView`, `SearchBox`, `ImportWizard`, `MainWindow` subscribe without unsubscribing
- **LINQ used in connection tree filtering** -- blueprint explicitly flags this as a hot path
- **Linux fallback keystore uses machine-id as sole encryption key** -- any local process can decrypt

---

## Data Flow

### Vault Unlock (GitHub Mode)
```
App Launch
    |
    v
Keychain.Retrieve("spotdesk:oauth:github")
    |
    +--> null: Show OAuthConnectDialog --> Device Flow --> Store token --> retry
    |
    v
OAuthService.GetCachedIdentityAsync() --> GitHub API /user --> userId
    |
    v
KeyDerivation.DeriveDeviceKey(userId, DeviceIdService.GetDeviceId())
    |
    v
Load vault.json --> Find DeviceEnvelope by deviceId
    |
    v
VaultCrypto.DecryptMasterKey(envelope.ciphertext, envelope.iv, deviceKey)
    |
    v
SessionLockService.SetMasterKey(masterKey)  [pinned in memory]
    |
    v
Vault Unlocked -- app opens normally
```

### Connection Lifecycle
```
User double-clicks connection in sidebar
    |
    v
MainWindowViewModel.OpenTab(entry)
    |
    v
SessionTabViewModel created --> ConnectAsync()
    |
    v
SessionManager.GetOrAdd(entry.Id)
    |
    +--> Existing session: reattach framebuffer (<50ms)
    |
    +--> New: IRdpBackend.CreateSession() / SshSession / VncClient
         |
         v
    session.ConnectAsync(entry, credential)
         |
         v
    SessionViewSelector.Build() --> RdpView / SshView / VncView
         |
         v
    FrameUpdated events --> WriteableBitmap --> Avalonia Image control
```

### Git Sync
```
Vault mutation (add/update/remove entry)
    |
    v
VaultService.SaveVaultAsync() --> write vault.json to disk
    |
    v
GitSyncService.SyncAsync(vaultPath)
    |
    v
LibGit2Sharp: Stage("*") --> Commit("spotdesk: sync [timestamp]") --> Push
    |
    +--> Fast-forward pull succeeds: done
    |
    +--> Diverged: ConflictResolver (NOT YET WIRED) would merge by updatedAt
    |
    +--> Offline: queue path, retry with exponential backoff
```
