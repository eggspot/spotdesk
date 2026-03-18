using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SpotDesk.Core.Auth;

public static class KeychainKeys
{
    public const string GitHub    = "spotdesk:oauth:github";
    public const string Bitbucket = "spotdesk:oauth:bitbucket";
    public const string Master    = "spotdesk:master";
    /// <summary>
    /// Fine-grained Personal Access Token scoped to the single vault repository.
    /// Stored separately so the GitHub sign-in token only needs read:user scope.
    /// </summary>
    public const string VaultRepoPat = "spotdesk:vault:repo_pat";
}

public interface IKeychainService
{
    void Store(string key, string value);
    string? Retrieve(string key);
    void Delete(string key);
}

public static class KeychainServiceFactory
{
    public static IKeychainService Create() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new WindowsKeychainService() :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new MacOsKeychainService() :
        new LinuxKeychainService();
}

[SupportedOSPlatform("windows")]
public class WindowsKeychainService : IKeychainService
{
    public void Store(string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var cred = new CredentialNative
        {
            TargetName = key,
            CredentialType = 1,
            Persist = 2,
            CredentialBlobSize = bytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(bytes.Length)
        };
        Marshal.Copy(bytes, 0, cred.CredentialBlob, bytes.Length);
        try { CredWrite(ref cred, 0); }
        finally { Marshal.FreeHGlobal(cred.CredentialBlob); }
    }

    public string? Retrieve(string key)
    {
        if (!CredRead(key, 1, 0, out var credPtr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CredentialNative>(credPtr);
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(credPtr); }
    }

    public void Delete(string key) => CredDelete(key, 1, 0);

    [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref CredentialNative credential, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialNative
    {
        public uint Flags;
        public uint CredentialType;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}

[SupportedOSPlatform("macos")]
public class MacOsKeychainService : IKeychainService
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";

    public void Store(string key, string value)
    {
        var valueBytes = Encoding.UTF8.GetBytes(value);
        SecKeychainAddGenericPassword(IntPtr.Zero, (uint)"spotdesk".Length, "spotdesk",
            (uint)key.Length, key, (uint)valueBytes.Length, valueBytes, IntPtr.Zero);
    }

    public string? Retrieve(string key)
    {
        var result = SecKeychainFindGenericPassword(IntPtr.Zero,
            (uint)"spotdesk".Length, "spotdesk",
            (uint)key.Length, key,
            out var length, out var data, IntPtr.Zero);
        if (result != 0) return null;

        var bytes = new byte[length];
        Marshal.Copy(data, bytes, 0, (int)length);
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        return Encoding.UTF8.GetString(bytes);
    }

    public void Delete(string key) { /* SecKeychainItemDelete */ }

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain,
        uint serviceNameLength, string serviceName,
        uint accountNameLength, string accountName,
        uint passwordLength, byte[] passwordData,
        IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain,
        uint serviceNameLength, string serviceName,
        uint accountNameLength, string accountName,
        out uint passwordLength, out IntPtr passwordData,
        IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
}

public class LinuxKeychainService : IKeychainService
{
    private readonly string _fallbackPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "spotdesk", "keystore");

    public void Store(string key, string value)
    {
        // Try D-Bus libsecret first
        if (TryDbusStore(key, value)) return;
        FallbackStore(key, value);
    }

    public string? Retrieve(string key)
    {
        var dbusResult = TryDbusRetrieve(key);
        return dbusResult ?? FallbackRetrieve(key);
    }

    public void Delete(string key)
    {
        // Remove from the encrypted file fallback.
        // D-Bus libsecret delete is a future enhancement when libsecret support is added.
        var store = LoadFallbackStore();
        if (store.Remove(key))
            SaveFallbackStore(store);
    }

    // D-Bus libsecret support is a TODO. The encrypted file fallback is always used.
    private static bool TryDbusStore(string key, string value) => false;
    private static string? TryDbusRetrieve(string key) => null;

    private void FallbackStore(string key, string value)
    {
        var store = LoadFallbackStore();
        store[key] = value;
        SaveFallbackStore(store);
    }

    private string? FallbackRetrieve(string key)
    {
        var store = LoadFallbackStore();
        return store.TryGetValue(key, out var value) ? value : null;
    }

    private Dictionary<string, string> LoadFallbackStore()
    {
        if (!File.Exists(_fallbackPath)) return new();
        var encrypted = File.ReadAllBytes(_fallbackPath);
        var key = GetFallbackKey();
        var json = Decrypt(encrypted, key);
        return System.Text.Json.JsonSerializer.Deserialize(json, KeychainJsonContext.Default.DictionaryStringString) ?? new();
    }

    private void SaveFallbackStore(Dictionary<string, string> store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(store, KeychainJsonContext.Default.DictionaryStringString);
        var key = GetFallbackKey();
        var encrypted = Encrypt(json, key);
        File.WriteAllBytes(_fallbackPath, encrypted);
    }

    private static byte[] GetFallbackKey()
    {
        var machineId = File.Exists("/etc/machine-id")
            ? File.ReadAllText("/etc/machine-id").Trim()
            : Environment.MachineName;
        return SHA256.HashData(Encoding.UTF8.GetBytes(machineId));
    }

    private static byte[] Encrypt(string plaintext, byte[] key)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length + 16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(iv, data, ciphertext.AsSpan(0, data.Length), ciphertext.AsSpan(data.Length));
        return [.. iv, .. ciphertext];
    }

    private static string Decrypt(byte[] encrypted, byte[] key)
    {
        var iv = encrypted[..12];
        var ciphertext = encrypted[12..];
        var plaintext = new byte[ciphertext.Length - 16];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(iv, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class KeychainJsonContext : JsonSerializerContext;
