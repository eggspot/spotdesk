using System.Collections.Frozen;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpotDesk.Core.Models;

namespace SpotDesk.UI.ViewModels;

public partial class ConnectionTreeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<ConnectionGroupViewModel> _groups = [];

    [ObservableProperty]
    private ObservableCollection<ConnectionEntry> _recents = [];

    [ObservableProperty]
    private ObservableCollection<ConnectionEntry> _filteredConnections = [];

    private FrozenDictionary<Guid, ConnectionEntry> _index = FrozenDictionary<Guid, ConnectionEntry>.Empty;

    partial void OnSearchQueryChanged(string value) => ApplyFilter(value);

    public void LoadConnections(IEnumerable<ConnectionGroup> groups, IEnumerable<ConnectionEntry> entries)
    {
        var entryList = entries.ToList();
        _index = entryList.ToFrozenDictionary(e => e.Id);

        Groups.Clear();
        foreach (var g in groups.OrderBy(g => g.SortOrder))
        {
            var groupEntries = entryList.Where(e => e.GroupId == g.Id).OrderBy(e => e.Name);
            Groups.Add(new ConnectionGroupViewModel(g, groupEntries, depth: 0));
        }

        // Favorites as first virtual group
        var favorites = entryList.Where(e => e.IsFavorite).OrderBy(e => e.Name).ToList();
        if (favorites.Count > 0)
            Groups.Insert(0, new ConnectionGroupViewModel(
                new ConnectionGroup { Name = "Favorites" }, favorites, depth: 0));
    }

    /// <summary>
    /// Adds a single new entry to the named group, creating the group if it doesn't exist.
    /// Called immediately after the New Connection dialog saves.
    /// </summary>
    public void AddEntry(ConnectionEntry entry, string groupName)
    {
        var name = string.IsNullOrWhiteSpace(groupName) ? "Default" : groupName.Trim();
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var groupVm = GetOrCreateGroupPath(parts, 0, Groups, depth: 0);
        entry.GroupId = groupVm.Group.Id;
        groupVm.Entries.Add(entry);
        RebuildIndex();
    }

    private static ConnectionGroupViewModel GetOrCreateGroupPath(
        string[] parts, int index,
        ObservableCollection<ConnectionGroupViewModel> collection,
        int depth)
    {
        var segment = parts[index];
        var existing = collection.FirstOrDefault(g =>
            string.Equals(g.Name, segment, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var group = new ConnectionGroup { Name = segment };
            existing = new ConnectionGroupViewModel(group, [], depth);
            collection.Add(existing);
        }

        if (index == parts.Length - 1) return existing;
        return GetOrCreateGroupPath(parts, index + 1, existing.Children, depth + 1);
    }

    private void ApplyFilter(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            FilteredConnections.Clear();
            return;
        }

        var q = query.ToLowerInvariant();
        var matches = _index.Values
            .Where(e => FuzzyMatch(e, q))
            .OrderByDescending(e => e.LastConnectedAt.GetValueOrDefault())
            .Take(20);

        FilteredConnections = [.. matches];
    }

    private static bool FuzzyMatch(ConnectionEntry entry, string query) =>
        entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Host.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Raised when the user clicks a connection row or its Connect button.
    /// MainWindowViewModel handles this by calling OpenTab.
    /// </summary>
    public event Action<ConnectionEntry>? ConnectionActivated;

    [RelayCommand]
    private void Connect(ConnectionEntry entry) => ConnectionActivated?.Invoke(entry);

    public event Action<ConnectionEntry>? EditRequested;
    public event Action<ConnectionGroupViewModel>? NewConnectionInGroupRequested;
    public event Action? NewConnectionRequested;

    [RelayCommand]
    private void Edit(ConnectionEntry entry) => EditRequested?.Invoke(entry);

    [RelayCommand]
    private void Delete(ConnectionEntry entry)
    {
        RemoveEntryFromTree(Groups, entry);
        RebuildIndex();
    }

    [RelayCommand]
    private void Duplicate(ConnectionEntry entry)
    {
        var copy = entry with
        {
            Id        = Guid.NewGuid(),
            Name      = entry.Name + " (copy)",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        AddEntry(copy, FindGroupName(entry) ?? "Default");
    }

    [RelayCommand]
    private void NewConnectionInGroup(ConnectionGroupViewModel groupVm) =>
        NewConnectionInGroupRequested?.Invoke(groupVm);

    [RelayCommand]
    private void DeleteGroup(ConnectionGroupViewModel groupVm) =>
        Groups.Remove(groupVm);

    [RelayCommand]
    private void NewConnection() => NewConnectionRequested?.Invoke();

    public void UpdateEntryGroup(ConnectionEntry entry, string newGroupName)
    {
        RemoveEntryFromTree(Groups, entry);
        AddEntry(entry, newGroupName);
    }

    private static bool RemoveEntryFromTree(
        IEnumerable<ConnectionGroupViewModel> groups,
        ConnectionEntry entry)
    {
        foreach (var g in groups)
        {
            if (g.Entries.Remove(entry)) return true;
            if (RemoveEntryFromTree(g.Children, entry)) return true;
        }
        return false;
    }

    public string? FindGroupName(ConnectionEntry entry) =>
        FindGroupNameRecursive(Groups, entry, string.Empty);

    private static string? FindGroupNameRecursive(
        IEnumerable<ConnectionGroupViewModel> groups,
        ConnectionEntry entry,
        string prefix)
    {
        foreach (var g in groups)
        {
            var path = prefix.Length > 0 ? $"{prefix}/{g.Name}" : g.Name;
            if (g.Entries.Contains(entry)) return path;
            var child = FindGroupNameRecursive(g.Children, entry, path);
            if (child is not null) return child;
        }
        return null;
    }

    private void RebuildIndex()
    {
        var all = new List<ConnectionEntry>();
        CollectEntries(Groups, all);
        _index = all.ToFrozenDictionary(e => e.Id);
    }

    private static void CollectEntries(
        IEnumerable<ConnectionGroupViewModel> groups,
        List<ConnectionEntry> target)
    {
        foreach (var g in groups)
        {
            target.AddRange(g.Entries);
            CollectEntries(g.Children, target);
        }
    }

    /// <summary>
    /// Raised when the sidebar header "Reconnect All" button is pressed.
    /// Handled by MainWindowViewModel.ReconnectAllCommand.
    /// </summary>
    public event Action? ReconnectAllRequested;

    [RelayCommand]
    private void ReconnectAll() => ReconnectAllRequested?.Invoke();

    /// <summary>
    /// Raised when the user presses Enter in the Quick Connect bar.
    /// MainWindowViewModel handles this by calling OpenTab.
    /// </summary>
    public event Action<ConnectionEntry>? QuickConnectRequested;

    [RelayCommand]
    private void QuickConnect(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        // Auto-detect protocol by port pattern
        var protocol = query.EndsWith(":22") ? Protocol.Ssh
            : query.EndsWith(":5900") ? Protocol.Vnc
            : Protocol.Rdp;

        var (host, port) = ParseHostPort(query, ConnectionEntry.DefaultPortFor(protocol));

        var entry = new ConnectionEntry
        {
            Name     = host,
            Host     = host,
            Port     = port,
            Protocol = protocol,
        };

        QuickConnectRequested?.Invoke(entry);
    }

    private static (string Host, int Port) ParseHostPort(string input, int defaultPort)
    {
        var lastColon = input.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(input[(lastColon + 1)..], out var port))
            return (input[..lastColon], port);
        return (input, defaultPort);
    }
}

public partial class ConnectionGroupViewModel : ObservableObject
{
    public ConnectionGroup Group { get; }
    public int Depth { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameBuffer = string.Empty;

    public ObservableCollection<ConnectionEntry> Entries { get; }
    public ObservableCollection<ConnectionGroupViewModel> Children { get; } = [];

    public ConnectionGroupViewModel(ConnectionGroup group, IEnumerable<ConnectionEntry> entries, int depth = 0)
    {
        Group       = group;
        Depth       = depth;
        _name       = group.Name;
        _isExpanded = group.IsExpanded;
        Entries     = new ObservableCollection<ConnectionEntry>(entries);
    }

    partial void OnNameChanged(string value) => Group.Name = value;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void BeginRename()
    {
        RenameBuffer = Name;
        IsRenaming   = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        var trimmed = RenameBuffer.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            Name = trimmed;
        IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
