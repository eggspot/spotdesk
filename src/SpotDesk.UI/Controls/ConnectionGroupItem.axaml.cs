using Avalonia.Controls;
using Avalonia.Input;
using SpotDesk.UI.ViewModels;

namespace SpotDesk.UI.Controls;

public partial class ConnectionGroupItem : UserControl
{
    public ConnectionGroupItem()
    {
        InitializeComponent();
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not ConnectionGroupViewModel vm) return;
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ConnectionGroupViewModel vm)
            vm.CommitRenameCommand.Execute(null);
    }
}
