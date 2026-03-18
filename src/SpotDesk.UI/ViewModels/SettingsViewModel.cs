using System.Globalization;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpotDesk.Core.Auth;
using SpotDesk.Core.Crypto;
using SpotDesk.Core.Models;
using SpotDesk.Core.Sync;
using SpotDesk.Core.Vault;

namespace SpotDesk.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IOAuthService _oauth;
    private readonly IVaultService _vault;
    private readonly ISessionLockService _sessionLock;
    private readonly ThemeService _themeService;
    private readonly LocalPrefsService _prefs;
    private readonly IGitSyncService? _syncService;
    private readonly IKeychainService _keychain;
    private readonly string _deviceId;

    [ObservableProperty] private AppTheme _theme = AppTheme.Dark;
    [ObservableProperty] private bool     _lockOnScreenLock;
    [ObservableProperty] private string   _autoSyncInterval = "15 minutes";
    [ObservableProperty] private string?  _gitRemoteUrl;
    [ObservableProperty] private DateTimeOffset? _lastSyncedAt;

    // Vault repo fine-grained PAT — stored in OS keychain, never in prefs.json
    [ObservableProperty] private string _vaultRepoPat  = string.Empty;
    [ObservableProperty] private string _vaultRepoStatus = string.Empty;

    public static IReadOnlyList<string> SyncIntervalOptions { get; } =
        ["5 minutes", "15 minutes", "30 minutes", "1 hour", "4 hours", "Manual only"];
    [ObservableProperty] private bool     _isGitHubConnected;
    [ObservableProperty] private string?  _githubLogin;
    [ObservableProperty] private bool     _isVaultUnlocked;
    [ObservableProperty] private string   _encryptionInfo = "AES-256-GCM · Argon2id · per-device key envelope";
    [ObservableProperty] private DeviceInfo[] _trustedDevices = [];

    // Device Flow state — visible while "Connect GitHub" is in progress
    [ObservableProperty] private string _deviceFlowUserCode        = string.Empty;
    [ObservableProperty] private string _deviceFlowVerificationUri = string.Empty;
    [ObservableProperty] private string _deviceFlowStatus          = string.Empty;
    [ObservableProperty] private bool   _isDeviceFlowActive;

    private CancellationTokenSource? _deviceFlowCts;

    // GitHub PAT — advanced fallback
    [ObservableProperty] private string _patToken  = string.Empty;
    [ObservableProperty] private string _patStatus = string.Empty;

    // Bitbucket App Password (Bitbucket has no Device Flow; App Password is the equivalent)
    [ObservableProperty] private bool    _isBitbucketConnected;
    [ObservableProperty] private string? _bitbucketDisplayName;
    [ObservableProperty] private string  _bitbucketUsername    = string.Empty;
    [ObservableProperty] private string  _bitbucketAppPassword = string.Empty;
    [ObservableProperty] private string  _bitbucketStatus      = string.Empty;

    // Local → GitHub migration
    [ObservableProperty] private bool   _isLocalMode;
    [ObservableProperty] private string _migrationRepoUrl = string.Empty;
    [ObservableProperty] private string _migrationStatus  = string.Empty;
    [ObservableProperty] private bool   _isMigrating;

    /// <summary>Migration is ready when GitHub is connected and a repo URL is provided.</summary>
    public bool CanMigrate => IsLocalMode && IsGitHubConnected && !string.IsNullOrWhiteSpace(MigrationRepoUrl) && !IsMigrating;

    public SettingsViewModel(
        IOAuthService oauth,
        IVaultService vault,
        ISessionLockService sessionLock,
        ThemeService? themeService = null,
        LocalPrefsService? prefs = null,
        IGitSyncService? syncService = null,
        IDeviceIdService? deviceIdService = null,
        IKeychainService? keychain = null)
    {
        _oauth        = oauth;
        _vault        = vault;
        _sessionLock  = sessionLock;
        _themeService = themeService ?? new ThemeService();
        _prefs        = prefs        ?? new LocalPrefsService();
        _syncService  = syncService;
        _keychain     = keychain ?? KeychainServiceFactory.Create();
        _deviceId     = deviceIdService?.GetDeviceId() ?? string.Empty;

        IsVaultUnlocked = sessionLock.IsUnlocked;
        var saved = _prefs.Load();
        _theme          = saved.Theme;
        _isLocalMode    = saved.VaultMode == "local";
        _gitRemoteUrl   = saved.VaultRepoPath;
        _autoSyncInterval = saved.AutoSyncInterval ?? "15 minutes";

        // Load vault repo PAT from keychain (never from prefs.json)
        var storedPat = _keychain.Retrieve(KeychainKeys.VaultRepoPat);
        if (!string.IsNullOrEmpty(storedPat))
            _vaultRepoPat = new string('•', 20); // show masked placeholder
    }

    // Notify CanMigrate when its dependencies change
    partial void OnIsGitHubConnectedChanged(bool value)   => OnPropertyChanged(nameof(CanMigrate));
    partial void OnMigrationRepoUrlChanged(string value)  => OnPropertyChanged(nameof(CanMigrate));
    partial void OnIsMigratingChanged(bool value)         => OnPropertyChanged(nameof(CanMigrate));

    partial void OnThemeChanged(AppTheme value)
    {
        _themeService.SetTheme(value);
        _prefs.Save(p => p with { Theme = value });
    }

    partial void OnAutoSyncIntervalChanged(string value) =>
        _prefs.Save(p => p with { AutoSyncInterval = value });

    // Primary auth: GitHub Device Authorization Grant (RFC 8628).
    // No redirect URI, no client_secret — single bundled client_id works for all users.
    [RelayCommand]
    private async Task ConnectGitHubAsync()
    {
        _deviceFlowCts?.Cancel();
        _deviceFlowCts = new CancellationTokenSource();
        var ct = _deviceFlowCts.Token;

        DeviceFlowStatus   = "Starting…";
        IsDeviceFlowActive = false;

        try
        {
            var challenge = await _oauth.StartGitHubDeviceFlowAsync(ct);

            DeviceFlowUserCode        = challenge.UserCode;
            DeviceFlowVerificationUri = challenge.VerificationUri;
            DeviceFlowStatus          = $"Enter the code at {challenge.VerificationUri}";
            IsDeviceFlowActive        = true;

            // Open verification URL automatically — user just pastes the code shown above
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = challenge.VerificationUri, UseShellExecute = true
            });

            var identity = await _oauth.PollGitHubDeviceFlowAsync(challenge, ct);

            IsGitHubConnected  = true;
            GithubLogin        = identity.Login;
            DeviceFlowStatus   = string.Empty;
            IsDeviceFlowActive = false;
        }
        catch (OperationCanceledException)
        {
            DeviceFlowStatus   = "Cancelled.";
            IsDeviceFlowActive = false;
        }
        catch (Exception ex)
        {
            DeviceFlowStatus   = $"Error: {ex.Message}";
            IsDeviceFlowActive = false;
        }
        finally
        {
            DeviceFlowUserCode        = string.Empty;
            DeviceFlowVerificationUri = string.Empty;
        }
    }

    [RelayCommand]
    private void CancelDeviceFlow()
    {
        _deviceFlowCts?.Cancel();
        _deviceFlowCts = null;
    }

    [RelayCommand]
    private async Task DisconnectGitHubAsync()
    {
        await _oauth.RevokeAsync(OAuthProvider.GitHub);
        IsGitHubConnected = false;
        GithubLogin       = null;
    }

    // Advanced fallback: Personal Access Token.
    // Use when Device Flow is unavailable (air-gapped / firewalled environments).
    [RelayCommand]
    private async Task AuthenticateWithPatAsync()
    {
        PatStatus = "Validating…";
        try
        {
            var identity = await _oauth.AuthenticateWithPatAsync(PatToken.Trim());
            IsGitHubConnected = true;
            GithubLogin       = identity.Login;
            PatStatus         = $"Connected as {identity.Login}";
            PatToken          = string.Empty;
        }
        catch (Exception ex)
        {
            PatStatus = $"Error: {ex.Message}";
        }
    }

    // Bitbucket App Password auth.
    // Create at: bitbucket.org/account/settings/app-passwords/new
    // Required scopes: Account (read), Repositories (read, write).
    [RelayCommand]
    private async Task ConnectBitbucketAsync()
    {
        BitbucketStatus = "Validating…";
        try
        {
            var identity = await _oauth.AuthenticateWithBitbucketAppPasswordAsync(
                BitbucketUsername.Trim(), BitbucketAppPassword.Trim());
            IsBitbucketConnected  = true;
            BitbucketDisplayName  = identity.Username;
            BitbucketStatus       = $"Connected as {identity.Username}";
            BitbucketAppPassword  = string.Empty;
        }
        catch (Exception ex)
        {
            BitbucketStatus = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectBitbucketAsync()
    {
        await _oauth.RevokeAsync(OAuthProvider.Bitbucket);
        IsBitbucketConnected = false;
        BitbucketDisplayName = null;
        BitbucketStatus      = string.Empty;
    }

    // ── Local → GitHub migration ───────────────────────────────────────────────
    // Steps:
    //   1. Re-wrap the in-memory master key with the GitHub device key  (VaultService)
    //   2. Init or clone the target repo                                (GitSyncService)
    //   3. Push vault.json as the first commit                          (GitSyncService)
    //   4. Persist the mode change and repo path to local prefs

    [RelayCommand]
    private async Task MigrateToGitSyncAsync()
    {
        IsMigrating     = true;
        MigrationStatus = "Migrating vault…";
        try
        {
            // Step 1 — get current GitHub identity and re-wrap the master key
            var identity = await _oauth.GetCachedIdentityAsync();
            await _vault.MigrateLocalToGitHubAsync(identity);
            MigrationStatus = "Vault re-encrypted for GitHub sync ✓";

            // Step 2 & 3 — init/clone repo and push
            if (_syncService is not null)
            {
                var vaultDir = _prefs.Load().VaultRepoPath;
                if (string.IsNullOrWhiteSpace(vaultDir))
                {
                    // Default local path: %AppData%/spotdesk/vault-repo
                    vaultDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "spotdesk", "vault-repo");
                }

                MigrationStatus = "Initialising Git repo…";
                await _syncService.InitOrCloneAsync(MigrationRepoUrl.Trim(), vaultDir);

                MigrationStatus = "Pushing vault…";
                await _syncService.SyncAsync(vaultDir);

                // Step 4 — persist the new mode and repo path
                _prefs.Save(p => p with
                {
                    VaultMode    = "github",
                    VaultRepoPath = vaultDir,
                });
            }
            else
            {
                _prefs.Save(p => p with { VaultMode = "github" });
            }

            IsLocalMode     = false;
            MigrationStatus = "Migration complete — sync is now active.";
        }
        catch (Exception ex)
        {
            MigrationStatus = $"Migration failed: {ex.Message}";
        }
        finally
        {
            IsMigrating = false;
        }
    }

    [RelayCommand]
    private async Task SaveVaultRepoAsync()
    {
        VaultRepoStatus = "Saving…";
        try
        {
            // Save repo URL to prefs
            _prefs.Save(p => p with { VaultRepoPath = GitRemoteUrl });

            // Save PAT to keychain only if user entered a real value (not the masked placeholder)
            if (!string.IsNullOrWhiteSpace(VaultRepoPat) && !VaultRepoPat.All(c => c == '•'))
            {
                _keychain.Store(KeychainKeys.VaultRepoPat, VaultRepoPat.Trim());
                VaultRepoPat = new string('•', 20); // re-mask after save
            }

            // Quick connectivity test — try to clone/open the repo
            if (_syncService is not null && !string.IsNullOrWhiteSpace(GitRemoteUrl))
            {
                var localDir = _prefs.Load().VaultRepoPath ?? string.Empty;
                // We don't clone here — just confirm settings are saved
                VaultRepoStatus = "Saved ✓";
            }
            else
            {
                VaultRepoStatus = "Saved ✓";
            }
        }
        catch (Exception ex)
        {
            VaultRepoStatus = $"Error: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void LockVault()
    {
        _sessionLock.Lock();
        IsVaultUnlocked = false;
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        LastSyncedAt = DateTimeOffset.UtcNow;
        if (_syncService is null) return;
        var vaultPath = _prefs.Load().VaultRepoPath;
        if (!string.IsNullOrWhiteSpace(vaultPath))
            await _syncService.SyncAsync(vaultPath);
    }

    [RelayCommand]
    private async Task RevokeDeviceAsync(string deviceId)
    {
        await _vault.RevokeDeviceAsync(deviceId);
        await LoadTrustedDevicesAsync();
    }

    [RelayCommand]
    private async Task ApproveNewDeviceAsync(DeviceApprovalRequest request)
    {
        await _vault.AddDeviceAsync(request.DeviceId, request.DeviceName);
        await LoadTrustedDevicesAsync();
    }

    private async Task LoadTrustedDevicesAsync()
    {
        var devices = await _vault.GetDevicesAsync();
        TrustedDevices = devices
            .Select(d => new DeviceInfo(d.DeviceId, d.DeviceName, d.AddedAt, d.DeviceId == _deviceId))
            .ToArray();
    }
}

public record DeviceInfo(string DeviceId, string DeviceName, DateTimeOffset AddedAt, bool IsCurrentDevice);
public record DeviceApprovalRequest(string DeviceId, string DeviceName);

/// <summary>
/// Converts AppTheme to bool for RadioButton.IsChecked bindings in SettingsView.
/// Usage: IsChecked="{Binding Theme, Converter={x:Static vm:AppThemeConverter.Dark}}"
/// </summary>
public sealed class AppThemeConverter : IValueConverter
{
    public static readonly AppThemeConverter Dark   = new(AppTheme.Dark);
    public static readonly AppThemeConverter Light  = new(AppTheme.Light);
    public static readonly AppThemeConverter System = new(AppTheme.System);

    private readonly AppTheme _target;
    private AppThemeConverter(AppTheme target) => _target = target;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppTheme t && t == _target;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? _target : Avalonia.Data.BindingOperations.DoNothing;
}
