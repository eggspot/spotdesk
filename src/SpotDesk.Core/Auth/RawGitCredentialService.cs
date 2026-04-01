using LibGit2Sharp;

namespace SpotDesk.Core.Auth;

public record RawGitIdentity(string RemoteUrl, string Username);

public interface IRawGitCredentialService
{
    void Store(string remoteUrl, string username, string password);
    (string Username, string Password)? Retrieve(string remoteUrl);
    void Delete(string remoteUrl);
    bool HasCredential(string remoteUrl);
    Task<RawGitIdentity> ValidateAsync(string remoteUrl, string username, string password, CancellationToken ct = default);
}

public class RawGitCredentialService : IRawGitCredentialService
{
    private readonly IKeychainService _keychain;

    public RawGitCredentialService(IKeychainService keychain) => _keychain = keychain;

    public void Store(string remoteUrl, string username, string password)
    {
        var key = NormalizeKey(remoteUrl);
        _keychain.Store(key, $"{username}\n{password}");
    }

    public (string Username, string Password)? Retrieve(string remoteUrl)
    {
        var key = NormalizeKey(remoteUrl);
        var stored = _keychain.Retrieve(key);
        if (stored is null) return null;

        var newlineIdx = stored.IndexOf('\n');
        if (newlineIdx < 0) return null;

        return (stored[..newlineIdx], stored[(newlineIdx + 1)..]);
    }

    public void Delete(string remoteUrl)
    {
        var key = NormalizeKey(remoteUrl);
        _keychain.Delete(key);
    }

    public bool HasCredential(string remoteUrl) => Retrieve(remoteUrl) is not null;

    public async Task<RawGitIdentity> ValidateAsync(
        string remoteUrl, string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new ArgumentException("Remote URL must not be empty.", nameof(remoteUrl));
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username must not be empty.", nameof(username));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password must not be empty.", nameof(password));

        await Task.Run(() =>
        {
            try
            {
                Repository.ListRemoteReferences(remoteUrl.Trim(), (_, _, _) =>
                    new UsernamePasswordCredentials
                    {
                        Username = username.Trim(),
                        Password = password.Trim()
                    });
            }
            catch (LibGit2SharpException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to authenticate with the remote repository: {ex.Message}", ex);
            }
        }, ct);

        return new RawGitIdentity(remoteUrl.Trim(), username.Trim());
    }

    private static string NormalizeKey(string remoteUrl) =>
        "rawgit:" + remoteUrl.Trim().ToLowerInvariant().TrimEnd('/');
}
