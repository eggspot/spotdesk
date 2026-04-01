using System.Security.Cryptography;
using System.Text.Json;
using SpotDesk.Core.Auth;
using SpotDesk.Core.Crypto;

namespace SpotDesk.Core.Vault;

public enum UnlockResult
{
    Success,
    NeedsOAuth,         // No GitHub token → trigger Device Flow or PAT setup
    NeedsDeviceApproval,// Device not registered in vault → need another device to approve
    NeedsPassword,      // Vault is in local mode → prompt for master password
    Failed,             // Decryption error (wrong password / corrupted vault)
}

public interface IVaultService
{
    /// <summary>
    /// Tries to unlock using the stored GitHub credential.
    /// Returns <see cref="UnlockResult.NeedsPassword"/> if the vault is in local mode.
    /// </summary>
    Task<UnlockResult> UnlockAsync(string vaultPath, CancellationToken ct = default);

    /// <summary>
    /// Unlocks a local-mode vault with the user's master password.
    /// Returns <see cref="UnlockResult.Success"/> or <see cref="UnlockResult.Failed"/>.
    /// </summary>
    Task<UnlockResult> UnlockLocalAsync(string password, string vaultPath, CancellationToken ct = default);

    Task FirstTimeSetupAsync(GitHubIdentity identity, string vaultPath, string repoUrl, CancellationToken ct = default);

    /// <summary>
    /// Creates a new local-mode vault encrypted with <paramref name="password"/>.
    /// No GitHub account required. Git sync is not available in this mode.
    /// </summary>
    Task FirstTimeSetupLocalAsync(string password, string vaultPath, CancellationToken ct = default);

    /// <summary>Returns all device envelopes currently registered in the vault.</summary>
    Task<IReadOnlyList<DeviceEnvelope>> GetDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Migrates an already-unlocked local-mode vault to GitHub sync mode.
    /// The master key stays the same — all existing entries remain valid.
    /// The password envelope is replaced with a device envelope derived from
    /// <paramref name="identity"/> so future unlocks use the GitHub token.
    /// </summary>
    Task MigrateLocalToGitHubAsync(GitHubIdentity identity, CancellationToken ct = default);
    Task AddDeviceAsync(string newDeviceId, string newDeviceName, CancellationToken ct = default);
    Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default);
    Task<VaultEntry> AddEntryAsync(string payloadJson, CancellationToken ct = default);
    Task<VaultEntry> UpdateEntryAsync(Guid id, string payloadJson, CancellationToken ct = default);
    Task RemoveEntryAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<(Guid Id, string Payload)>> GetAllEntriesAsync(CancellationToken ct = default);
}

public class VaultService : IVaultService
{
    private readonly IKeychainService _keychain;
    private readonly IDeviceIdService _deviceId;
    private readonly IKeyDerivationService _kdf;
    private readonly IOAuthService _oauth;
    private readonly ISessionLockService _lock;

    private VaultFile? _vault;
    private string? _vaultPath;

    public VaultService(
        IKeychainService keychain,
        IDeviceIdService deviceId,
        IKeyDerivationService kdf,
        IOAuthService oauth,
        ISessionLockService sessionLock)
    {
        _keychain = keychain;
        _deviceId = deviceId;
        _kdf = kdf;
        _oauth = oauth;
        _lock = sessionLock;
    }

    public async Task<UnlockResult> UnlockAsync(string vaultPath, CancellationToken ct = default)
    {
        _vaultPath = vaultPath;

        // If a vault already exists, let it self-describe its mode before touching keychain
        if (File.Exists(vaultPath))
        {
            var peek = await LoadVaultAsync(vaultPath, ct);
            if (peek.Mode == "local")
                return UnlockResult.NeedsPassword;
        }

        var token = _keychain.Retrieve(KeychainKeys.GitHub);
        if (token is null)
            return UnlockResult.NeedsOAuth;

        GitHubIdentity identity;
        try
        {
            identity = await _oauth.GetCachedIdentityAsync(ct);
        }
        catch
        {
            return UnlockResult.NeedsOAuth;
        }

        if (!File.Exists(vaultPath))
            return UnlockResult.NeedsOAuth; // triggers FirstTimeSetupAsync

        _vault = await LoadVaultAsync(vaultPath, ct);

        var deviceId = _deviceId.GetDeviceId();
        var envelope = _vault.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (envelope is null)
            return UnlockResult.NeedsDeviceApproval;

        try
        {
            var deviceKey = _kdf.DeriveDeviceKey(identity.UserId, deviceId);
            var encKey = Convert.FromBase64String(envelope.EncryptedMasterKey);
            var iv = Convert.FromBase64String(envelope.Iv);
            var masterKey = VaultCrypto.DecryptMasterKey(encKey, iv, deviceKey);
            _lock.SetMasterKey(masterKey);
            return UnlockResult.Success;
        }
        catch
        {
            return UnlockResult.Failed;
        }
    }

    public async Task FirstTimeSetupAsync(GitHubIdentity identity, string vaultPath, string repoUrl, CancellationToken ct = default)
    {
        _vaultPath = vaultPath;
        var deviceId = _deviceId.GetDeviceId();
        var deviceKey = _kdf.DeriveDeviceKey(identity.UserId, deviceId);

        var masterKey = VaultCrypto.GenerateMasterKey();
        _lock.SetMasterKey(masterKey);

        var (ciphertext, iv) = VaultCrypto.EncryptMasterKey(masterKey, deviceKey);

        var envelope = new DeviceEnvelope
        {
            DeviceId = deviceId,
            DeviceName = Environment.MachineName,
            EncryptedMasterKey = Convert.ToBase64String(ciphertext),
            Iv = Convert.ToBase64String(iv)
        };

        _vault = new VaultFile { Devices = [envelope], Entries = [] };
        await SaveVaultAsync(ct);
    }

    public async Task FirstTimeSetupLocalAsync(string password, string vaultPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Master password must not be empty.", nameof(password));

        _vaultPath = vaultPath;

        // Generate a random salt unique to this vault — stored in vault.json (not secret)
        var salt       = RandomNumberGenerator.GetBytes(32);
        var passwordKey = _kdf.DeriveFromPassword(password, salt);

        var masterKey = VaultCrypto.GenerateMasterKey();
        _lock.SetMasterKey(masterKey);

        var (ciphertext, iv) = VaultCrypto.EncryptMasterKey(masterKey, passwordKey);

        var envelope = new DeviceEnvelope
        {
            DeviceId           = "local",
            DeviceName         = Environment.MachineName,
            EncryptedMasterKey = Convert.ToBase64String(ciphertext),
            Iv                 = Convert.ToBase64String(iv),
        };

        _vault = new VaultFile
        {
            Mode    = "local",
            Salt    = Convert.ToBase64String(salt),
            Devices = [envelope],
            Entries = [],
        };
        await SaveVaultAsync(ct);
    }

    public async Task<UnlockResult> UnlockLocalAsync(string password, string vaultPath, CancellationToken ct = default)
    {
        _vaultPath = vaultPath;

        if (!File.Exists(vaultPath))
            return UnlockResult.Failed;

        _vault = await LoadVaultAsync(vaultPath, ct);

        if (_vault.Mode != "local" || _vault.Salt is null)
            return UnlockResult.Failed;

        var envelope = _vault.Devices.FirstOrDefault(d => d.DeviceId == "local");
        if (envelope is null)
            return UnlockResult.Failed;

        try
        {
            var salt        = Convert.FromBase64String(_vault.Salt);
            var passwordKey = _kdf.DeriveFromPassword(password, salt);
            var encKey      = Convert.FromBase64String(envelope.EncryptedMasterKey);
            var iv          = Convert.FromBase64String(envelope.Iv);
            var masterKey   = VaultCrypto.DecryptMasterKey(encKey, iv, passwordKey);
            _lock.SetMasterKey(masterKey);
            return UnlockResult.Success;
        }
        catch
        {
            // Wrong password → AES-GCM auth tag mismatch
            return UnlockResult.Failed;
        }
    }

    public Task<IReadOnlyList<DeviceEnvelope>> GetDevicesAsync(CancellationToken ct = default)
    {
        EnsureUnlocked();
        return Task.FromResult<IReadOnlyList<DeviceEnvelope>>(_vault!.Devices);
    }

    public async Task MigrateLocalToGitHubAsync(GitHubIdentity identity, CancellationToken ct = default)
    {
        EnsureUnlocked();

        // Re-wrap the in-memory master key with a GitHub-derived device key.
        // Every existing VaultEntry ciphertext is untouched — same master key.
        var deviceId  = _deviceId.GetDeviceId();
        var deviceKey = _kdf.DeriveDeviceKey(identity.UserId, deviceId);
        var masterKey = _lock.GetMasterKey().ToArray();
        var (ciphertext, iv) = VaultCrypto.EncryptMasterKey(masterKey, deviceKey);

        var envelope = new DeviceEnvelope
        {
            DeviceId           = deviceId,
            DeviceName         = Environment.MachineName,
            EncryptedMasterKey = Convert.ToBase64String(ciphertext),
            Iv                 = Convert.ToBase64String(iv),
        };

        // Drop the password-based "local" envelope; store GitHub device envelope.
        // Clearing Salt prevents any future password-based decryption attempt.
        _vault = _vault! with
        {
            Mode    = "github",
            Salt    = null,
            Devices = [envelope],
        };

        await SaveVaultAsync(ct);
    }

    public async Task AddDeviceAsync(string newDeviceId, string newDeviceName, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var identity = await _oauth.GetCachedIdentityAsync(ct);

        var newDeviceKey = _kdf.DeriveDeviceKey(identity.UserId, newDeviceId);
        var masterKey = _lock.GetMasterKey().ToArray();
        var (ciphertext, iv) = VaultCrypto.EncryptMasterKey(masterKey, newDeviceKey);

        var newEnvelope = new DeviceEnvelope
        {
            DeviceId = newDeviceId,
            DeviceName = newDeviceName,
            EncryptedMasterKey = Convert.ToBase64String(ciphertext),
            Iv = Convert.ToBase64String(iv)
        };

        _vault = _vault! with { Devices = [.. _vault.Devices, newEnvelope] };
        await SaveVaultAsync(ct);
    }

    public async Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        EnsureUnlocked();
        _vault = _vault! with { Devices = _vault.Devices.Where(d => d.DeviceId != deviceId).ToArray() };
        await SaveVaultAsync(ct);
    }

    public async Task<VaultEntry> AddEntryAsync(string payloadJson, CancellationToken ct = default)
    {
        EnsureUnlocked();
        var (ciphertext, iv) = VaultCrypto.EncryptEntry(payloadJson, _lock.GetMasterKey().ToArray());
        var entry = new VaultEntry
        {
            Id = Guid.NewGuid(),
            Ciphertext = Convert.ToBase64String(ciphertext),
            Iv = Convert.ToBase64String(iv)
        };
        _vault = _vault! with { Entries = [.. _vault.Entries, entry] };
        await SaveVaultAsync(ct);
        return entry;
    }

    public async Task<VaultEntry> UpdateEntryAsync(Guid id, string payloadJson, CancellationToken ct = default)
    {
        EnsureUnlocked();
        await RemoveEntryAsync(id, ct);
        var (ciphertext, iv) = VaultCrypto.EncryptEntry(payloadJson, _lock.GetMasterKey().ToArray());
        var entry = new VaultEntry
        {
            Id = id,
            Ciphertext = Convert.ToBase64String(ciphertext),
            Iv = Convert.ToBase64String(iv)
        };
        _vault = _vault! with { Entries = [.. _vault.Entries, entry] };
        await SaveVaultAsync(ct);
        return entry;
    }

    public async Task RemoveEntryAsync(Guid id, CancellationToken ct = default)
    {
        EnsureUnlocked();
        _vault = _vault! with { Entries = _vault.Entries.Where(e => e.Id != id).ToArray() };
        await SaveVaultAsync(ct);
    }

    public Task<IReadOnlyList<(Guid Id, string Payload)>> GetAllEntriesAsync(CancellationToken ct = default)
    {
        EnsureUnlocked();
        var masterKey = _lock.GetMasterKey().ToArray();
        var results = _vault!.Entries
            .Select(e => (e.Id, VaultCrypto.DecryptEntry(
                Convert.FromBase64String(e.Ciphertext),
                Convert.FromBase64String(e.Iv),
                masterKey)))
            .ToList();
        return Task.FromResult<IReadOnlyList<(Guid, string)>>(results);
    }

    private void EnsureUnlocked()
    {
        if (!_lock.IsUnlocked)
            throw new InvalidOperationException("Vault is locked.");
        if (_vault is null || _vaultPath is null)
            throw new InvalidOperationException("Vault not loaded.");
    }

    private static async Task<VaultFile> LoadVaultAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, VaultJsonContext.Default.VaultFile, ct)
               ?? throw new InvalidDataException("Invalid vault file.");
    }

    private async Task SaveVaultAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_vaultPath!);
        if (dir is not null) Directory.CreateDirectory(dir);

        await using var stream = File.Create(_vaultPath!);
        await JsonSerializer.SerializeAsync(stream, _vault!, VaultJsonContext.Default.VaultFile, ct);
    }
}
