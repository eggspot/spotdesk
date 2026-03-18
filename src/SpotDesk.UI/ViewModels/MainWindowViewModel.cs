using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpotDesk.Core.Models;
using SpotDesk.Core.Sync;
using SpotDesk.Protocols;

namespace SpotDesk.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IGitSyncService? _syncService;
    private readonly LocalPrefsService _prefs;
    private readonly ThemeService _themeService;
    private readonly ISessionManager? _sessionManager;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(EffectiveSidebarWidth))]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(EffectiveSidebarWidth))]
    [NotifyPropertyChangedFor(nameof(SidebarPinIcon))]
    private bool _isSidebarPinned = true;

    [ObservableProperty]
    private double _sidebarWidth = 260;

    public bool IsSidebarOverlayOpen => !IsSidebarPinned && IsSidebarVisible;
    public double EffectiveSidebarWidth => IsSidebarPinned && IsSidebarVisible ? SidebarWidth : 0;
    public string SidebarPinIcon => IsSidebarPinned ? "📌" : "◈";

    [ObservableProperty]
    private SessionTabViewModel? _activeTab;

    [ObservableProperty]
    private bool _isWelcomeVisible = true;

    [ObservableProperty]
    private bool _isSearchVisible;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string _syncStatusText = string.Empty;

    /// <summary>DI-injected ConnectionTreeViewModel — wired into SidebarView.</summary>
    public ConnectionTreeViewModel ConnectionTree { get; }

    /// <summary>ViewModel for the global search overlay (Ctrl+K).</summary>
    public SearchViewModel Search { get; }

    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    public MainWindowViewModel(
        ConnectionTreeViewModel connectionTree,
        SearchViewModel? search = null,
        IGitSyncService? syncService = null,
        LocalPrefsService? prefs = null,
        ThemeService? themeService = null,
        ISessionManager? sessionManager = null)
    {
        ConnectionTree   = connectionTree;
        Search           = search ?? new SearchViewModel([]);
        _syncService     = syncService;
        _prefs           = prefs ?? new LocalPrefsService();
        _themeService    = themeService ?? new ThemeService();
        _sessionManager  = sessionManager;

        // Subscribe to search activation
        Search.ConnectionActivated += entry => OpenTab(entry);
        Search.CloseRequested      += () => IsSearchVisible = false;

        // Sidebar row click or Connect button
        ConnectionTree.ConnectionActivated += OpenTab;
        ConnectionTree.EditRequested           += entry => EditConnectionRequested?.Invoke(entry);
        ConnectionTree.NewConnectionRequested  += () => NewConnectionRequested?.Invoke();
        ConnectionTree.NewConnectionInGroupRequested += groupVm =>
            NewConnectionInGroupRequested?.Invoke(groupVm.Group.Name);

        // Quick Connect bar opens an ad-hoc tab directly
        ConnectionTree.QuickConnectRequested += OpenTab;

        // Restore saved preferences
        var saved = _prefs.Load();
        _themeService.SetTheme(saved.Theme);
        _isSidebarVisible  = saved.SidebarVisible;
        _isSidebarPinned   = saved.SidebarPinned;
        _sidebarWidth      = saved.SidebarWidth;
    }

    // ── Navigation ────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleSidebarPin() => IsSidebarPinned = !IsSidebarPinned;

    partial void OnIsSidebarVisibleChanged(bool value)
    {
        _prefs.Save(p => p with { SidebarVisible = value });
        OnPropertyChanged(nameof(IsSidebarOverlayOpen));
        OnPropertyChanged(nameof(EffectiveSidebarWidth));
    }

    partial void OnIsSidebarPinnedChanged(bool value)
    {
        _prefs.Save(p => p with { SidebarPinned = value });
        OnPropertyChanged(nameof(IsSidebarOverlayOpen));
        OnPropertyChanged(nameof(EffectiveSidebarWidth));
        OnPropertyChanged(nameof(SidebarPinIcon));
    }

    public void SaveSidebarWidth(double width)
    {
        SidebarWidth = Math.Clamp(width, 160, 520);
        OnPropertyChanged(nameof(EffectiveSidebarWidth));
        _prefs.Save(p => p with { SidebarWidth = SidebarWidth });
    }

    [RelayCommand]
    private void NewConnection()
    {
        // Raised to the View layer; the Window opens NewConnectionDialog
        NewConnectionRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenSearch()
    {
        IsSearchVisible = true;
        SearchOpenRequested?.Invoke();
    }

    // ── Sync ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ForceSyncAsync()
    {
        if (_syncService is null) return;
        IsSyncing     = true;
        SyncStatusText = "Syncing…";
        try
        {
            var savedPath = _prefs.Load().VaultRepoPath;
            if (!string.IsNullOrEmpty(savedPath))
                await _syncService.SyncAsync(savedPath);
            SyncStatusText = "Synced";
        }
        catch (Exception ex)
        {
            SyncStatusText = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    // ── Tab management ────────────────────────────────────────────────────

    /// <summary>
    /// Called by the View after New Connection dialog saves.
    /// Adds the entry to the sidebar tree, then opens a tab and starts connecting.
    /// </summary>
    public void AddNewConnection(ConnectionEntry entry, string groupName)
    {
        ConnectionTree.AddEntry(entry, groupName);
        OpenTab(entry);
    }

    public void OpenTab(ConnectionEntry connection)
    {
        var existing = Tabs.FirstOrDefault(t => t.ConnectionId == connection.Id);
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        var tab = new SessionTabViewModel(connection, _sessionManager);
        tab.CloseRequested += () => CloseTab(tab);
        Tabs.Add(tab);
        ActiveTab        = tab;
        IsWelcomeVisible = false;

        _ = tab.ConnectCommand.ExecuteAsync(null);
    }

    public void CloseTab(SessionTabViewModel tab)
    {
        Tabs.Remove(tab);
        tab.Dispose();
        ActiveTab = Tabs.LastOrDefault();
        if (Tabs.Count == 0) IsWelcomeVisible = true;
    }

    public void SwitchToTab(int index)
    {
        if (index >= 0 && index < Tabs.Count)
            ActiveTab = Tabs[index];
    }

    // ── Tab switching commands (Ctrl+1 through Ctrl+9) ────────────────────

    [RelayCommand] private void SwitchToTab1() => SwitchToTab(0);
    [RelayCommand] private void SwitchToTab2() => SwitchToTab(1);
    [RelayCommand] private void SwitchToTab3() => SwitchToTab(2);
    [RelayCommand] private void SwitchToTab4() => SwitchToTab(3);
    [RelayCommand] private void SwitchToTab5() => SwitchToTab(4);
    [RelayCommand] private void SwitchToTab6() => SwitchToTab(5);
    [RelayCommand] private void SwitchToTab7() => SwitchToTab(6);
    [RelayCommand] private void SwitchToTab8() => SwitchToTab(7);
    [RelayCommand] private void SwitchToTab9() => SwitchToTab(8);

    // ── Reconnect All ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ReconnectAllAsync()
    {
        var disconnected = Tabs
            .Where(t => t.Status is SessionStatus.Error or SessionStatus.Idle)
            .ToList();

        foreach (var tab in disconnected)
            await tab.ReconnectCommand.ExecuteAsync(null);
    }

    // ── GitHub sign-in ────────────────────────────────────────────────────

    [RelayCommand]
    private void SignInWithGitHub() => GitHubSignInRequested?.Invoke();

    // ── Import ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenImport() => ImportRequested?.Invoke();

    // ── Events raised to the View layer ──────────────────────────────────

    public event Action? NewConnectionRequested;
    public event Action? SettingsRequested;
    public event Action? SearchOpenRequested;
    public event Action? GitHubSignInRequested;
    public event Action? ImportRequested;
    public event Action<ConnectionEntry>? EditConnectionRequested;
    public event Action<string>? NewConnectionInGroupRequested;
}
