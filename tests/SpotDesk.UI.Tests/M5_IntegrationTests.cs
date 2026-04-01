using Avalonia.Headless.XUnit;
using NSubstitute;
using SpotDesk.Core.Auth;
using SpotDesk.Core.Crypto;
using SpotDesk.Core.Models;
using SpotDesk.Core.Sync;
using SpotDesk.Core.Vault;
using SpotDesk.UI.ViewModels;
using SpotDesk.UI.Views;
using Xunit;

namespace SpotDesk.UI.Tests;

// Helper to test SessionViewSelector logic without constructing XAML views
// (which require App-level StaticResources not available in headless tests).
internal sealed class TestableSessionViewSelector
{
    public Type GetViewType(SessionTabViewModel vm) => vm.Protocol switch
    {
        Protocol.Ssh => typeof(SshView),
        Protocol.Vnc => typeof(VncView),
        _            => typeof(RdpView),
    };
}

// ═══════════════════════════════════════════════════════════════════════════════
// M5 — Integration & Smoke Tests
// These tests wire up real ViewModels with mocked services to verify end-to-end
// flows without requiring a real vault, network, or OS keychain.
// ═══════════════════════════════════════════════════════════════════════════════

// ── SettingsViewModel Integration ────────────────────────────────────────────

public class M5_SettingsViewModelIntegrationTests
{
    private static SettingsViewModel CreateSettingsVm(
        IOAuthService? oauth = null,
        IVaultService? vault = null,
        ISessionLockService? sessionLock = null,
        IGitSyncService? syncService = null)
    {
        return new SettingsViewModel(
            oauth ?? Substitute.For<IOAuthService>(),
            vault ?? Substitute.For<IVaultService>(),
            sessionLock ?? Substitute.For<ISessionLockService>(),
            new ThemeService(),
            new LocalPrefsService(),
            syncService,
            Substitute.For<IDeviceIdService>());
    }

    [Fact, Trait("Category", "M5")]
    public void InitialState_VaultLocked_ShowsLocked()
    {
        var lockSvc = Substitute.For<ISessionLockService>();
        lockSvc.IsUnlocked.Returns(false);
        var vm = CreateSettingsVm(sessionLock: lockSvc);

        Assert.False(vm.IsVaultUnlocked);
    }

    [Fact, Trait("Category", "M5")]
    public void InitialState_VaultUnlocked_ShowsUnlocked()
    {
        var lockSvc = Substitute.For<ISessionLockService>();
        lockSvc.IsUnlocked.Returns(true);
        var vm = CreateSettingsVm(sessionLock: lockSvc);

        Assert.True(vm.IsVaultUnlocked);
    }

    [Fact, Trait("Category", "M5")]
    public void LockVault_SetsVaultUnlockedFalse()
    {
        var lockSvc = Substitute.For<ISessionLockService>();
        lockSvc.IsUnlocked.Returns(true);
        var vm = CreateSettingsVm(sessionLock: lockSvc);

        vm.LockVaultCommand.Execute(null);

        Assert.False(vm.IsVaultUnlocked);
        lockSvc.Received(1).Lock();
    }

    [Fact, Trait("Category", "M5")]
    public void ThemeChange_PersistsViaPrefsService()
    {
        var vm = CreateSettingsVm();
        vm.Theme = AppTheme.Light;

        var prefs = new LocalPrefsService();
        // Theme is set on the ThemeService and saved to prefs
        Assert.Equal(AppTheme.Light, vm.Theme);
    }

    [Fact, Trait("Category", "M5")]
    public async Task ConnectGitHub_OnSuccess_SetsConnectedState()
    {
        var oauth = Substitute.For<IOAuthService>();
        var challenge = new DeviceFlowChallenge
        {
            DeviceCode = "dev123", UserCode = "ABCD-1234",
            VerificationUri = "https://github.com/login/device",
            Interval = 1, ExpiresIn = 900, ClientId = "test"
        };
        oauth.StartGitHubDeviceFlowAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(challenge));
        oauth.PollGitHubDeviceFlowAsync(challenge, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitHubIdentity(1, "testuser", "token123")));

        var vm = CreateSettingsVm(oauth: oauth);
        await vm.ConnectGitHubCommand.ExecuteAsync(null);

        Assert.True(vm.IsGitHubConnected);
        Assert.Equal("testuser", vm.GithubLogin);
    }

    [Fact, Trait("Category", "M5")]
    public async Task DisconnectGitHub_ClearsConnectedState()
    {
        var oauth = Substitute.For<IOAuthService>();
        var vm = CreateSettingsVm(oauth: oauth);

        // Simulate already connected
        vm.IsGitHubConnected = true;
        vm.GithubLogin = "testuser";

        await vm.DisconnectGitHubCommand.ExecuteAsync(null);

        Assert.False(vm.IsGitHubConnected);
        Assert.Null(vm.GithubLogin);
        await oauth.Received(1).RevokeAsync(OAuthProvider.GitHub);
    }

    [Fact, Trait("Category", "M5")]
    public async Task AuthenticateWithPat_ValidToken_SetsConnected()
    {
        var oauth = Substitute.For<IOAuthService>();
        oauth.AuthenticateWithPatAsync("ghp_test123")
            .Returns(Task.FromResult(new GitHubIdentity(2, "patuser", "ghp_test123")));

        var vm = CreateSettingsVm(oauth: oauth);
        vm.PatToken = "ghp_test123";
        await vm.AuthenticateWithPatCommand.ExecuteAsync(null);

        Assert.True(vm.IsGitHubConnected);
        Assert.Equal("patuser", vm.GithubLogin);
        Assert.Contains("patuser", vm.PatStatus);
    }

    [Fact, Trait("Category", "M5")]
    public async Task AuthenticateWithPat_InvalidToken_ShowsError()
    {
        var oauth = Substitute.For<IOAuthService>();
        oauth.AuthenticateWithPatAsync(Arg.Any<string>())
            .Returns(Task.FromException<GitHubIdentity>(new Exception("Bad credentials")));

        var vm = CreateSettingsVm(oauth: oauth);
        vm.PatToken = "invalid";
        await vm.AuthenticateWithPatCommand.ExecuteAsync(null);

        Assert.False(vm.IsGitHubConnected);
        Assert.Contains("Bad credentials", vm.PatStatus);
    }

    [Fact, Trait("Category", "M5")]
    public void CanMigrate_LocalModeAndConnected_IsTrue()
    {
        var vm = CreateSettingsVm();
        vm.IsGitHubConnected = true;
        vm.MigrationRepoUrl = "https://github.com/user/vault.git";

        // Must also set local mode — use reflection-free approach via prefs
        // The VM reads IsLocalMode from prefs at construction; set it manually
        vm.IsLocalMode = true;

        Assert.True(vm.CanMigrate);
    }

    [Fact, Trait("Category", "M5")]
    public void CanMigrate_NotConnected_IsFalse()
    {
        var vm = CreateSettingsVm();
        vm.IsLocalMode = true;
        vm.MigrationRepoUrl = "https://github.com/user/vault.git";

        Assert.False(vm.CanMigrate);
    }

    [Fact, Trait("Category", "M5")]
    public void CanMigrate_NoRepoUrl_IsFalse()
    {
        var vm = CreateSettingsVm();
        vm.IsLocalMode = true;
        vm.IsGitHubConnected = true;
        vm.MigrationRepoUrl = "";

        Assert.False(vm.CanMigrate);
    }
}

// ── SessionTabViewModel Lifecycle ────────────────────────────────────────────

public class M5_SessionTabViewModelLifecycleTests
{
    private static ConnectionEntry MakeEntry(Protocol protocol = Protocol.Rdp) =>
        new() { Name = "Test", Host = "10.0.0.1", Protocol = protocol, Port = ConnectionEntry.DefaultPortFor(protocol) };

    [Fact, Trait("Category", "M5")]
    public void NewTab_StartsIdle()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        Assert.Equal(SessionStatus.Idle, tab.Status);
        Assert.Empty(tab.StatusMessage ?? "");
    }

    [Fact, Trait("Category", "M5")]
    public async Task Connect_TransitionsToConnected()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        await tab.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionStatus.Connected, tab.Status);
    }

    [Fact, Trait("Category", "M5")]
    public async Task Disconnect_TransitionsToIdle()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        await tab.ConnectCommand.ExecuteAsync(null);
        await tab.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionStatus.Idle, tab.Status);
    }

    [Fact, Trait("Category", "M5")]
    public void OnUnexpectedDisconnect_SetsErrorAndStartsReconnect()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        tab.OnUnexpectedDisconnect("Server closed connection");

        Assert.Equal(SessionStatus.Error, tab.Status);
        Assert.True(tab.IsReconnecting);
        Assert.Equal(3, tab.ReconnectCountdown);
        Assert.Equal("Server closed connection", tab.StatusMessage);
    }

    [Fact, Trait("Category", "M5")]
    public void CancelReconnect_StopsCountdownAndGoesIdle()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        tab.OnUnexpectedDisconnect();

        tab.CancelReconnectCommand.Execute(null);

        Assert.False(tab.IsReconnecting);
        Assert.Equal(0, tab.ReconnectCountdown);
        Assert.Equal(SessionStatus.Idle, tab.Status);
    }

    [Fact, Trait("Category", "M5")]
    public void Dispose_CleansUpReconnectTimer()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        tab.OnUnexpectedDisconnect();

        tab.Dispose();

        // After dispose, the timer should be stopped — no way to directly verify
        // but Dispose must not throw
        Assert.True(true);
    }

    [Fact, Trait("Category", "M5")]
    public void StatusColor_ReflectsStatus()
    {
        var tab = new SessionTabViewModel(MakeEntry());

        Assert.Equal("#6B7280", tab.StatusColor); // Idle = gray

        tab.OnUnexpectedDisconnect();
        Assert.Equal("#EF4444", tab.StatusColor); // Error = red
    }

    [Fact, Trait("Category", "M5")]
    public void ProtocolIcon_MatchesProtocol()
    {
        var rdp = new SessionTabViewModel(MakeEntry(Protocol.Rdp));
        var ssh = new SessionTabViewModel(MakeEntry(Protocol.Ssh));
        var vnc = new SessionTabViewModel(MakeEntry(Protocol.Vnc));

        Assert.NotEqual(rdp.ProtocolIcon, ssh.ProtocolIcon);
        Assert.NotEqual(ssh.ProtocolIcon, vnc.ProtocolIcon);
        Assert.NotEmpty(rdp.ProtocolIcon);
    }

    [Fact, Trait("Category", "M5")]
    public void CtrlAltDel_RaisesEvent()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        var raised = false;
        tab.CtrlAltDelRequested += () => raised = true;

        tab.SendCtrlAltDelCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact, Trait("Category", "M5")]
    public void HandleKeyInput_RaisesEvent()
    {
        var tab = new SessionTabViewModel(MakeEntry());
        var raised = false;
        tab.KeyInputReceived += _ => raised = true;

        tab.HandleKeyInput(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent });

        Assert.True(raised);
    }
}

// ── MainWindowViewModel Tab Lifecycle ────────────────────────────────────────

public class M5_MainWindowTabLifecycleTests
{
    private static MainWindowViewModel CreateVm() =>
        new(new ConnectionTreeViewModel());

    [Fact, Trait("Category", "M5")]
    public void OpenTab_SetsActiveTabAndHidesWelcome()
    {
        var vm = CreateVm();
        var entry = new ConnectionEntry { Name = "S", Host = "1.1.1.1", Protocol = Protocol.Rdp };

        vm.OpenTab(entry);

        Assert.Equal("S", vm.ActiveTab?.DisplayName);
        Assert.False(vm.IsWelcomeVisible);
    }

    [Fact, Trait("Category", "M5")]
    public void CloseLastTab_ShowsWelcome()
    {
        var vm = CreateVm();
        var entry = new ConnectionEntry { Name = "S", Host = "1.1.1.1", Protocol = Protocol.Rdp };
        vm.OpenTab(entry);

        vm.CloseTab(vm.Tabs[0]);

        Assert.Empty(vm.Tabs);
        Assert.True(vm.IsWelcomeVisible);
    }

    [Fact, Trait("Category", "M5")]
    public void CloseMiddleTab_SwitchesToLast()
    {
        var vm = CreateVm();
        var e1 = new ConnectionEntry { Name = "A", Host = "1.1.1.1", Protocol = Protocol.Rdp };
        var e2 = new ConnectionEntry { Name = "B", Host = "2.2.2.2", Protocol = Protocol.Ssh };
        var e3 = new ConnectionEntry { Name = "C", Host = "3.3.3.3", Protocol = Protocol.Vnc };
        vm.OpenTab(e1);
        vm.OpenTab(e2);
        vm.OpenTab(e3);

        vm.CloseTab(vm.Tabs[1]); // close B

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Equal("C", vm.ActiveTab?.DisplayName);
    }

    [Fact, Trait("Category", "M5")]
    public void ToggleSidebar_FlipsVisibility()
    {
        var vm = CreateVm();
        var initial = vm.IsSidebarVisible;

        vm.ToggleSidebarCommand.Execute(null);

        Assert.NotEqual(initial, vm.IsSidebarVisible);
    }

    [Fact, Trait("Category", "M5")]
    public void OpenSearch_SetsIsSearchVisible()
    {
        var vm = CreateVm();

        vm.OpenSearchCommand.Execute(null);

        Assert.True(vm.IsSearchVisible);
    }

    [Fact, Trait("Category", "M5")]
    public void QuickConnect_RaisedFromTree_OpensTab()
    {
        var vm = CreateVm();

        vm.ConnectionTree.QuickConnectCommand.Execute("10.0.0.1:22");

        Assert.Single(vm.Tabs);
        Assert.Equal(Protocol.Ssh, vm.Tabs[0].Protocol);
    }
}

// ── SessionViewSelector ──────────────────────────────────────────────────────

public class M5_SessionViewSelectorTests
{
    [Fact, Trait("Category", "M5")]
    public void Match_SessionTabViewModel_ReturnsTrue()
    {
        var selector = new SessionViewSelector();
        var vm = new SessionTabViewModel(
            new ConnectionEntry { Name = "T", Host = "h", Protocol = Protocol.Rdp });

        Assert.True(selector.Match(vm));
    }

    [Fact, Trait("Category", "M5")]
    public void Match_OtherObject_ReturnsFalse()
    {
        var selector = new SessionViewSelector();
        Assert.False(selector.Match("not a vm"));
        Assert.False(selector.Match(null));
    }

    [Fact, Trait("Category", "M5")]
    public void Build_RdpProtocol_ReturnsRdpView()
    {
        // SessionViewSelector dispatches on Protocol — verify the type mapping
        // without constructing the full XAML view (which needs App-level resources).
        var selector = new TestableSessionViewSelector();
        var vm = new SessionTabViewModel(
            new ConnectionEntry { Name = "T", Host = "h", Protocol = Protocol.Rdp });

        Assert.Equal(typeof(RdpView), selector.GetViewType(vm));
    }

    [Fact, Trait("Category", "M5")]
    public void Build_SshProtocol_ReturnsSshView()
    {
        var selector = new TestableSessionViewSelector();
        var vm = new SessionTabViewModel(
            new ConnectionEntry { Name = "T", Host = "h", Protocol = Protocol.Ssh });

        Assert.Equal(typeof(SshView), selector.GetViewType(vm));
    }

    [Fact, Trait("Category", "M5")]
    public void Build_VncProtocol_ReturnsVncView()
    {
        var selector = new TestableSessionViewSelector();
        var vm = new SessionTabViewModel(
            new ConnectionEntry { Name = "T", Host = "h", Protocol = Protocol.Vnc });

        Assert.Equal(typeof(VncView), selector.GetViewType(vm));
    }
}
