using Avalonia.Controls;
using SpotDesk.Core.Models;
using SpotDesk.UI.ViewModels;

namespace SpotDesk.UI.Dialogs;

public partial class NewConnectionDialog : Window
{
    public ConnectionEntry?   ResultEntry      { get; private set; }
    public CredentialEntry?   ResultCredential { get; private set; }

    private ConnectionEntry? _editingEntry;

    /// <summary>New connection (optionally pre-selecting a group).</summary>
    public NewConnectionDialog(string? defaultGroup = null)
    {
        InitializeComponent();
        var vm = new NewConnectionDialogViewModel();
        if (!string.IsNullOrWhiteSpace(defaultGroup))
            vm.Group = defaultGroup;
        DataContext = vm;
    }

    /// <summary>Edit an existing connection.</summary>
    public NewConnectionDialog(ConnectionEntry entry, string groupName)
    {
        InitializeComponent();
        _editingEntry = entry;
        var vm = new NewConnectionDialogViewModel();
        vm.LoadFromEntry(entry, groupName);
        DataContext = vm;
    }

    private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not NewConnectionDialogViewModel vm) return;

        if (_editingEntry is not null)
        {
            vm.ApplyToEntry(_editingEntry);
            ResultEntry = _editingEntry;
        }
        else
        {
            ResultEntry = vm.BuildEntry();
        }

        ResultCredential = vm.BuildCredential();
        Close(true);
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}
