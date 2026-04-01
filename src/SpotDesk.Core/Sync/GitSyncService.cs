using LibGit2Sharp;
using SpotDesk.Core.Auth;

namespace SpotDesk.Core.Sync;

public enum SyncEvent { SyncStarted, SyncCompleted, SyncFailed, ConflictResolved }

public interface IGitSyncService
{
    event Action<SyncEvent, string?> OnSyncEvent;
    Task InitOrCloneAsync(string repoUrl, string localPath, CancellationToken ct = default);
    Task SyncAsync(string localPath, CancellationToken ct = default);
    Task FlushPendingAsync(CancellationToken ct = default);
}

/// <summary>
/// Git sync powered by LibGit2Sharp — no external git CLI required.
/// LibGit2Sharp bundles the native libgit2 binary for each platform,
/// so the app works as a single executable with no external dependencies.
/// Native libs are extracted from the single-file bundle at startup via
/// <c>IncludeNativeLibrariesForSelfExtract</c>.
/// </summary>
public class GitSyncService : IGitSyncService
{
    private readonly IKeychainService _keychain;
    private readonly IRawGitCredentialService? _rawGit;

    // ── Offline queue ─────────────────────────────────────────────────────
    // When offline, paths are queued. FlushPendingAsync drains the queue
    // with exponential backoff on repeated failures.
    private readonly Queue<string> _pendingPaths = new();
    private int    _consecutiveFailures;
    private const int MaxBackoffSeconds = 300; // 5 minutes

    public event Action<SyncEvent, string?> OnSyncEvent = delegate { };

    public GitSyncService(IKeychainService keychain, IRawGitCredentialService? rawGit = null)
    {
        _keychain = keychain;
        _rawGit = rawGit;
    }

    /// <summary>
    /// Returns the best available credentials for git operations.
    /// Priority: vault-repo fine-grained PAT → GitHub identity token → raw git credentials for the remote URL.
    /// </summary>
    private Credentials ResolveCredentials(string url)
    {
        // Try GitHub token-based auth first
        var token = _keychain.Retrieve(KeychainKeys.VaultRepoPat)
            ?? _keychain.Retrieve(KeychainKeys.GitHub);

        if (!string.IsNullOrEmpty(token))
            return new UsernamePasswordCredentials { Username = "x-token", Password = token };

        // Fall back to raw git credentials for this remote URL
        if (_rawGit is not null)
        {
            var rawCred = _rawGit.Retrieve(url);
            if (rawCred is not null)
                return new UsernamePasswordCredentials { Username = rawCred.Value.Username, Password = rawCred.Value.Password };
        }

        throw new InvalidOperationException(
            "No credentials available for git operations. " +
            "Sign in with GitHub or configure a Raw Git credential in Settings.");
    }

    public async Task InitOrCloneAsync(string repoUrl, string localPath, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            if (Repository.IsValid(localPath)) return;

            var options = new CloneOptions
            {
                FetchOptions = { CredentialsProvider = (_, _, _) => ResolveCredentials(repoUrl) }
            };
            Repository.Clone(repoUrl, localPath, options);
        }, ct);
    }

    public async Task SyncAsync(string localPath, CancellationToken ct = default)
    {
        OnSyncEvent(SyncEvent.SyncStarted, null);
        try
        {
            await Task.Run(() =>
            {
                using var repo = new Repository(localPath);
                var remote = repo.Network.Remotes["origin"];
                var remoteUrl = remote?.Url ?? string.Empty;

                // Pull — fast-forward only
                var pullOptions = new PullOptions
                {
                    FetchOptions = new FetchOptions
                    {
                        CredentialsProvider = (_, _, _) => ResolveCredentials(remoteUrl)
                    },
                    MergeOptions = new MergeOptions { FastForwardStrategy = FastForwardStrategy.FastForwardOnly }
                };

                var sig = BuildSignature();
                Commands.Pull(repo, sig, pullOptions);

                // Stage all changes and commit only when there are actual changes
                Commands.Stage(repo, "*");
                var hasStagedChanges = repo.Diff.Compare<TreeChanges>(
                    repo.Head.Tip?.Tree, DiffTargets.Index).Count > 0;
                if (hasStagedChanges)
                {
                    var timestamp = DateTimeOffset.UtcNow.ToString("o");
                    repo.Commit($"spotdesk: sync {timestamp}", sig, sig);
                }

                // Push
                var pushOptions = new PushOptions
                {
                    CredentialsProvider = (_, _, _) => ResolveCredentials(remoteUrl)
                };
                repo.Network.Push(repo.Head, pushOptions);
            }, ct);

            _consecutiveFailures = 0;
            OnSyncEvent(SyncEvent.SyncCompleted, null);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _pendingPaths.Enqueue(localPath);
            OnSyncEvent(SyncEvent.SyncFailed, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Retry all queued paths with exponential backoff.
    /// Call this on reconnect or periodically from the background sync timer.
    /// </summary>
    public async Task FlushPendingAsync(CancellationToken ct = default)
    {
        if (_pendingPaths.Count == 0) return;

        // Exponential backoff: 2^failures seconds, capped at MaxBackoffSeconds
        var backoff = Math.Min(
            (int)Math.Pow(2, _consecutiveFailures),
            MaxBackoffSeconds);
        await Task.Delay(TimeSpan.FromSeconds(backoff), ct);

        var snapshot = _pendingPaths.ToArray();
        _pendingPaths.Clear();

        foreach (var path in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncAsync(path, ct);
            }
            catch
            {
                // Re-queued inside SyncAsync on failure — continue trying others
            }
        }
    }

    private static Signature BuildSignature() =>
        new("SpotDesk", "sync@spotdesk.app", DateTimeOffset.UtcNow);
}
