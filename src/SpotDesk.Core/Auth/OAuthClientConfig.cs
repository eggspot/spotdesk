namespace SpotDesk.Core.Auth;

/// <summary>
/// Holds OAuth application credentials supplied by the user at runtime.
/// Injected via DI so tests and different deployments can swap them out.
/// </summary>
public record OAuthClientConfig
{
    public string GitHubClientId { get; init; } = string.Empty;

    /// <summary>
    /// Public client ID registered for the SpotDesk GitHub OAuth App (Device Flow).
    /// Leave empty until an OAuth App is registered; users can supply their own via Settings.
    /// </summary>
    internal const string BundledGitHubClientId = "Ov23li3yeDNYtHGPz0d7";

    /// <summary>True when at least the GitHub client ID has been configured.</summary>
    public bool IsGitHubConfigured  => !string.IsNullOrWhiteSpace(GitHubClientId);

    /// <summary>
    /// Builds a config by merging a user-saved record with environment-variable overrides.
    /// Environment variables take precedence so CI pipelines can inject secrets without
    /// touching the on-disk prefs file.
    /// </summary>
    public static OAuthClientConfig Resolve(string? savedGitHubClientId) => new()
    {
        GitHubClientId = Coalesce(
            Environment.GetEnvironmentVariable("SPOTDESK_GITHUB_CLIENT_ID"),
            savedGitHubClientId),
    };

    private static string Coalesce(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        return string.Empty;
    }
}
