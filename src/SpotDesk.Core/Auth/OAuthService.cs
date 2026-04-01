using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SpotDesk.Core.Auth;

public record GitHubIdentity(long UserId, string Login, string AccessToken);

public enum OAuthProvider { GitHub }

public interface IOAuthService
{
    // ── Browser / redirect flows ──────────────────────────────────────────
    Task<GitHubIdentity> AuthenticateGitHubAsync(CancellationToken ct = default);

    // ── PAT (Personal Access Token) — no OAuth App required ──────────────
    /// <summary>
    /// Validates a GitHub Personal Access Token against the API, stores it in the
    /// keychain and caches the identity — no browser redirect needed.
    /// </summary>
    Task<GitHubIdentity> AuthenticateWithPatAsync(string pat, CancellationToken ct = default);

    // ── Device Authorization Grant — works without a redirect URI ────────
    /// <summary>
    /// Step 1: asks GitHub for a device code and user code.
    /// Show <see cref="DeviceFlowChallenge.UserCode"/> and
    /// <see cref="DeviceFlowChallenge.VerificationUri"/> to the user.
    /// </summary>
    Task<DeviceFlowChallenge> StartGitHubDeviceFlowAsync(CancellationToken ct = default);

    /// <summary>
    /// Step 2: polls GitHub until the user approves (or the code expires).
    /// Respects <see cref="DeviceFlowChallenge.Interval"/> and back-off on slow_down.
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is cancelled.
    /// </summary>
    Task<GitHubIdentity> PollGitHubDeviceFlowAsync(DeviceFlowChallenge challenge, CancellationToken ct = default);

    // ── Shared ────────────────────────────────────────────────────────────
    Task<GitHubIdentity> GetCachedIdentityAsync(CancellationToken ct = default);
    Task<bool> IsAuthenticatedAsync(OAuthProvider provider, CancellationToken ct = default);
    Task RevokeAsync(OAuthProvider provider);
}

public class OAuthService : IOAuthService
{
    private readonly IKeychainService _keychain;
    private readonly HttpClient _http;
    private OAuthClientConfig _config;

    private GitHubIdentity? _githubCache;
    private DateTimeOffset _githubCacheExpiry;

    public OAuthService(IKeychainService keychain, OAuthClientConfig? config = null, HttpClient? http = null)
    {
        _keychain = keychain;
        _config   = config ?? OAuthClientConfig.Resolve(null);
        _http     = http ?? new HttpClient();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SpotDesk/1.0");
    }

    /// <summary>
    /// Hot-reloads client credentials when the user saves new values in Settings.
    /// </summary>
    public void UpdateConfig(OAuthClientConfig config) => _config = config;

    public async Task<GitHubIdentity> AuthenticateGitHubAsync(CancellationToken ct = default)
    {
        var port        = GetFreePort();
        var redirectUri = $"http://localhost:{port}/callback";
        var (verifier, challenge) = GeneratePkce();
        var state       = GenerateState();

        if (!_config.IsGitHubConfigured)
            throw new InvalidOperationException(
                "GitHub Client ID is not configured. " +
                "Go to Settings → OAuth to enter your GitHub OAuth App credentials.");

        var authUrl = "https://github.com/login/oauth/authorize" +
            $"?client_id={Uri.EscapeDataString(_config.GitHubClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString("read:user")}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256" +
            $"&response_type=code";

        var code = await RunBrowserFlowAsync(authUrl, port, state, ct);

        // GitHub PKCE token exchange — no client_secret required
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = _config.GitHubClientId,
                ["code"]          = code,
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = verifier,
            })
        };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var tokenData = await resp.Content.ReadFromJsonAsync(GitHubTokenContext.Default.GitHubTokenResponse, ct)
            ?? throw new InvalidDataException("GitHub token response was null");

        _keychain.Store(KeychainKeys.GitHub, tokenData.AccessToken);
        return await FetchGitHubIdentityAsync(tokenData.AccessToken, ct);
    }

    // ── PAT (Personal Access Token) ──────────────────────────────────────────

    public async Task<GitHubIdentity> AuthenticateWithPatAsync(string pat, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pat))
            throw new ArgumentException("Personal Access Token must not be empty.", nameof(pat));

        var identity = await FetchGitHubIdentityAsync(pat.Trim(), ct);
        _keychain.Store(KeychainKeys.GitHub, pat.Trim());
        return identity;
    }

    // ── Device Authorization Grant ────────────────────────────────────────────

    public async Task<DeviceFlowChallenge> StartGitHubDeviceFlowAsync(CancellationToken ct = default)
    {
        var clientId = ResolveDeviceFlowClientId();

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"]     = "read:user",
            })
        };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var data = await resp.Content.ReadFromJsonAsync(DeviceFlowJsonContext.Default.DeviceCodeResponse, ct)
            ?? throw new InvalidDataException("Device code response was null.");

        return new DeviceFlowChallenge
        {
            DeviceCode      = data.DeviceCode,
            UserCode        = data.UserCode,
            VerificationUri = data.VerificationUri,
            ExpiresIn       = data.ExpiresIn,
            Interval        = data.Interval,
            ClientId        = clientId,
        };
    }

    public async Task<GitHubIdentity> PollGitHubDeviceFlowAsync(
        DeviceFlowChallenge challenge, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(challenge.Interval, 0));

        while (!ct.IsCancellationRequested)
        {
            if (interval > TimeSpan.Zero)
                await Task.Delay(interval, ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"]   = challenge.ClientId,
                    ["device_code"] = challenge.DeviceCode,
                    ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
                })
            };
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var data = await resp.Content.ReadFromJsonAsync(DeviceFlowJsonContext.Default.DeviceTokenPollResponse, ct)
                ?? throw new InvalidDataException("Device flow poll response was null.");

            if (!string.IsNullOrEmpty(data.AccessToken))
            {
                _keychain.Store(KeychainKeys.GitHub, data.AccessToken);
                return await FetchGitHubIdentityAsync(data.AccessToken, ct);
            }

            switch (data.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new InvalidOperationException(
                        "Device flow authorization expired. Please start the sign-in again.");
                case "access_denied":
                    throw new InvalidOperationException(
                        "Device flow authorization was denied.");
                default:
                    throw new InvalidOperationException(
                        $"Device flow error: {data.Error ?? "unknown"}");
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    private string ResolveDeviceFlowClientId()
    {
        // Prefer user-configured → bundled public client ID
        if (_config.IsGitHubConfigured) return _config.GitHubClientId;
        if (!string.IsNullOrWhiteSpace(OAuthClientConfig.BundledGitHubClientId))
            return OAuthClientConfig.BundledGitHubClientId;

        throw new InvalidOperationException(
            "No GitHub Client ID available. " +
            "Go to Settings → OAuth to enter your GitHub OAuth App Client ID, " +
            "or set the SPOTDESK_GITHUB_CLIENT_ID environment variable.");
    }

    public async Task<GitHubIdentity> GetCachedIdentityAsync(CancellationToken ct = default)
    {
        if (_githubCache is not null && DateTimeOffset.UtcNow < _githubCacheExpiry)
            return _githubCache;

        var token = _keychain.Retrieve(KeychainKeys.GitHub)
            ?? throw new InvalidOperationException("No GitHub token in keychain.");

        return await FetchGitHubIdentityAsync(token, ct);
    }

    public Task<bool> IsAuthenticatedAsync(OAuthProvider provider, CancellationToken ct = default)
    {
        return Task.FromResult(_keychain.Retrieve(KeychainKeys.GitHub) is not null);
    }

    public Task RevokeAsync(OAuthProvider provider)
    {
        _keychain.Delete(KeychainKeys.GitHub);
        _githubCache = null;
        return Task.CompletedTask;
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private async Task<GitHubIdentity> FetchGitHubIdentityAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new("token", token);

        var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var user = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubUserResponse, ct)
            ?? throw new InvalidDataException("GitHub user API returned null");

        var identity = new GitHubIdentity(user.Id, user.Login, token);
        _githubCache       = identity;
        _githubCacheExpiry = DateTimeOffset.UtcNow.AddHours(24);
        return identity;
    }

    private static async Task<string> RunBrowserFlowAsync(
        string authUrl, int port, string expectedState, CancellationToken ct)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = authUrl,
            UseShellExecute = true
        });

        var prefix   = $"http://localhost:{port}/callback/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            var contextTask = listener.GetContextAsync();
            await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cts.Token));

            if (!contextTask.IsCompletedSuccessfully)
                throw new OperationCanceledException("OAuth timed out.");

            var context = await contextTask;
            var rawUrl  = context.Request.RawUrl ?? string.Empty;

            var html = "<html><body><h2>SpotDesk: Authentication complete. You can close this tab.</h2></body></html>"u8.ToArray();
            context.Response.ContentType      = "text/html";
            context.Response.ContentLength64  = html.Length;
            await context.Response.OutputStream.WriteAsync(html, CancellationToken.None);
            context.Response.Close();

            var query = ParseQuery(rawUrl);

            if (query.TryGetValue("error", out var oauthError))
                throw new InvalidOperationException($"OAuth denied: {oauthError}");

            if (!query.TryGetValue("state", out var returnedState) || returnedState != expectedState)
                throw new InvalidOperationException("OAuth state mismatch.");

            if (!query.TryGetValue("code", out var code))
                throw new InvalidOperationException("No code in OAuth callback.");

            return code;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Dictionary<string, string> ParseQuery(string rawUrl)
    {
        var result = new Dictionary<string, string>();
        var idx    = rawUrl.IndexOf('?');
        if (idx < 0) return result;
        foreach (var part in rawUrl[(idx + 1)..].Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            result[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return result;
    }

    private static (string verifier, string challenge) GeneratePkce()
    {
        var verifier  = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

// ── JSON models ───────────────────────────────────────────────────────────────

internal record GitHubUserResponse
{
    [JsonPropertyName("id")]    public long   Id    { get; init; }
    [JsonPropertyName("login")] public string Login { get; init; } = string.Empty;
}

internal record GitHubTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
    [JsonPropertyName("token_type")]   public string TokenType   { get; init; } = string.Empty;
    [JsonPropertyName("scope")]        public string Scope        { get; init; } = string.Empty;
}

[JsonSerializable(typeof(GitHubUserResponse))]
internal partial class GitHubJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(GitHubTokenResponse))]
internal partial class GitHubTokenContext : JsonSerializerContext;

internal record DeviceCodeResponse
{
    [JsonPropertyName("device_code")]      public string DeviceCode      { get; init; } = string.Empty;
    [JsonPropertyName("user_code")]        public string UserCode        { get; init; } = string.Empty;
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; init; } = string.Empty;
    [JsonPropertyName("expires_in")]       public int    ExpiresIn       { get; init; } = 900;
    [JsonPropertyName("interval")]         public int    Interval        { get; init; } = 5;
}

internal record DeviceTokenPollResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("error")]        public string? Error       { get; init; }
}

[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(DeviceTokenPollResponse))]
internal partial class DeviceFlowJsonContext : JsonSerializerContext;
