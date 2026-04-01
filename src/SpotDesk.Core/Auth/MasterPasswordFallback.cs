using SpotDesk.Core.Crypto;

namespace SpotDesk.Core.Auth;

/// <summary>
/// Optional fallback when the user cannot use GitHub OAuth.
/// Uses the same Argon2id parameters as the device-key path.
/// </summary>
public class MasterPasswordFallback
{
    private readonly IKeyDerivationService _kdf;

    public MasterPasswordFallback(IKeyDerivationService kdf) => _kdf = kdf;

    /// <summary>
    /// Derives a 32-byte master key from the user's password and a random vault salt.
    /// The salt is stored (unencrypted) in VaultFile.Salt.
    /// </summary>
    public byte[] DeriveKey(string password, byte[] salt) =>
        _kdf.DeriveFromPassword(password, salt);
}
