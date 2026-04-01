using FluentAssertions;
using SpotDesk.Core.Auth;
using SpotDesk.Core.Tests.TestHelpers;
using Xunit;

namespace SpotDesk.Core.Tests;

[Trait("Category", "M1")]
public class RawGitCredentialTests
{
    private static RawGitCredentialService BuildService(InMemoryKeychainService? keychain = null)
        => new(keychain ?? new InMemoryKeychainService());

    [Fact]
    public void Store_Retrieve_Roundtrip()
    {
        var svc = BuildService();
        svc.Store("https://github.com/user/repo.git", "alice", "s3cret");

        var cred = svc.Retrieve("https://github.com/user/repo.git");

        cred.Should().NotBeNull();
        cred!.Value.Username.Should().Be("alice");
        cred!.Value.Password.Should().Be("s3cret");
    }

    [Fact]
    public void HasCredential_TrueAfterStore_FalseAfterDelete()
    {
        var svc = BuildService();
        var url = "https://gitlab.com/org/vault.git";

        svc.HasCredential(url).Should().BeFalse();

        svc.Store(url, "bob", "p@ssword");
        svc.HasCredential(url).Should().BeTrue();

        svc.Delete(url);
        svc.HasCredential(url).Should().BeFalse();
    }

    [Fact]
    public void Retrieve_ReturnsNull_WhenNotStored()
    {
        var svc = BuildService();
        svc.Retrieve("https://example.com/repo.git").Should().BeNull();
    }

    [Fact]
    public void Store_NormalizesUrl_CaseAndTrailingSlash()
    {
        var svc = BuildService();
        svc.Store("https://GitHub.COM/User/Repo/", "alice", "pass");

        // Retrieve with different casing and no trailing slash
        var cred = svc.Retrieve("https://github.com/user/repo");

        cred.Should().NotBeNull();
        cred!.Value.Username.Should().Be("alice");
    }

    [Fact]
    public void Password_WithNewline_IsStoredAndRetrievedCorrectly()
    {
        // Passwords should not contain newlines (they're the separator),
        // but the first newline is treated as the separator.
        var svc = BuildService();
        svc.Store("https://example.com/repo.git", "user", "line1\nline2");

        var cred = svc.Retrieve("https://example.com/repo.git");

        cred.Should().NotBeNull();
        cred!.Value.Username.Should().Be("user");
        cred!.Value.Password.Should().Be("line1\nline2");
    }

    [Fact]
    public async Task ValidateAsync_EmptyUrl_ThrowsArgumentException()
    {
        var svc = BuildService();

        await svc.Invoking(s => s.ValidateAsync("", "user", "pass"))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Remote URL*");
    }

    [Fact]
    public async Task ValidateAsync_EmptyUsername_ThrowsArgumentException()
    {
        var svc = BuildService();

        await svc.Invoking(s => s.ValidateAsync("https://example.com/repo.git", "", "pass"))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Username*");
    }

    [Fact]
    public async Task ValidateAsync_EmptyPassword_ThrowsArgumentException()
    {
        var svc = BuildService();

        await svc.Invoking(s => s.ValidateAsync("https://example.com/repo.git", "user", "  "))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Password*");
    }
}
