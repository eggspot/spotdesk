using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SpotDesk.Core.Models;
using SpotDesk.UI.Controls;
using SpotDesk.UI.Dialogs;
using SpotDesk.UI.ViewModels;

namespace SpotDesk.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Set programmatic app icon (window chrome + title bar inline image)
        try
        {
            var bmp = AppIconGenerator.CreateBitmap();
            Icon = new WindowIcon(bmp);
            var titleBarIcon = this.FindControl<Avalonia.Controls.Image>("TitleBarIcon");
            if (titleBarIcon != null) titleBarIcon.Source = bmp;
        }
        catch { /* non-fatal */ }

        // Wire drag region — the tab bar area (excluding interactive controls) moves the window
        var titleBar = this.FindControl<Border>("TitleBar");
        titleBar?.AddHandler(PointerPressedEvent, OnTitleBarPointerPressed, handledEventsToo: false);

        // Wire custom window buttons
        var minimize = this.FindControl<Button>("MinimizeButton");
        var maximize = this.FindControl<Button>("MaximizeButton");
        var close    = this.FindControl<Button>("CloseButton");

        if (minimize != null) minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        if (maximize != null) maximize.Click += (_, _) => ToggleMaximize();
        if (close    != null) close.Click    += (_, _) => Close();

        // Keep maximize icon in sync with window state
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty)
                UpdateMaximizeIcon();
        };

        var resizeHandle = this.FindControl<Border>("SidebarResizeHandle");
        if (resizeHandle != null)
        {
            resizeHandle.AddHandler(PointerPressedEvent,  OnResizePressed,  handledEventsToo: false);
            resizeHandle.AddHandler(PointerMovedEvent,    OnResizeMoved,    handledEventsToo: false);
            resizeHandle.AddHandler(PointerReleasedEvent, OnResizeReleased, handledEventsToo: false);
        }

        var backdrop = this.FindControl<Border>("SidebarBackdrop");
        if (backdrop != null)
            backdrop.AddHandler(PointerPressedEvent, OnBackdropPressed, handledEventsToo: false);

        if (DataContext is not MainWindowViewModel vm) return;

        vm.NewConnectionRequested += OnNewConnectionRequested;
        vm.SettingsRequested      += OnSettingsRequested;
        vm.SearchOpenRequested    += OnSearchOpenRequested;
        vm.GitHubSignInRequested  += OnGitHubSignInRequested;
        vm.ImportRequested                 += OnImportRequested;
        vm.EditConnectionRequested         += OnEditConnectionRequested;
        vm.NewConnectionInGroupRequested   += OnNewConnectionInGroupRequested;
    }

    private bool   _isDraggingResize;
    private double _dragStartX;
    private double _dragStartWidth;

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (DataContext is not MainWindowViewModel vm) return;
        _isDraggingResize = true;
        _dragStartX       = e.GetPosition(this).X;
        _dragStartWidth   = vm.SidebarWidth;
        e.Pointer.Capture(sender as Border);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingResize) return;
        if (DataContext is not MainWindowViewModel vm) return;
        var delta = e.GetPosition(this).X - _dragStartX;
        vm.SidebarWidth = Math.Clamp(_dragStartWidth + delta, 160, 520);
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingResize) return;
        _isDraggingResize = false;
        if (DataContext is MainWindowViewModel vm)
            vm.SaveSidebarWidth(vm.SidebarWidth);
        e.Pointer.Capture(null);
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsSidebarVisible = false;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeIcon()
    {
        var icon = this.FindControl<TextBlock>("MaximizeIcon");
        if (icon != null)
            icon.Text = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private async void OnEditConnectionRequested(SpotDesk.Core.Models.ConnectionEntry entry)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var currentGroup = vm.ConnectionTree.FindGroupName(entry) ?? "Default";
        var dialog = new NewConnectionDialog(entry, currentGroup);
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed) return;
        var newGroup = (dialog.DataContext as SpotDesk.UI.ViewModels.NewConnectionDialogViewModel)?.Group ?? currentGroup;
        if (!string.Equals(newGroup, currentGroup, StringComparison.OrdinalIgnoreCase))
            vm.ConnectionTree.UpdateEntryGroup(entry, newGroup);
    }

    private async void OnNewConnectionInGroupRequested(string groupName)
    {
        var dialog = new NewConnectionDialog(groupName);
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!confirmed || dialog.ResultEntry is not { } entry) return;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.AddNewConnection(entry, groupName);
    }

    private async void OnNewConnectionRequested()
    {
        var dialog    = new NewConnectionDialog();
        var confirmed = await dialog.ShowDialog<bool>(this);

        if (!confirmed || dialog.ResultEntry is not { } entry) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var groupName = (dialog.DataContext as SpotDesk.UI.ViewModels.NewConnectionDialogViewModel)?.Group
                        ?? "Default";

        vm.AddNewConnection(entry, groupName);
    }

    private async void OnSettingsRequested()
    {
        if (DataContext is MainWindowViewModel)
        {
            var settingsVm = AppServices.GetRequired<SettingsViewModel>();
            var dialog = new Window
            {
                Title                 = "Settings",
                Width                 = 720,
                Height                = 540,
                Background            = this.Background,
                Content               = new SettingsView { DataContext = settingsVm },
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            try { dialog.Icon = AppIconGenerator.Create(); } catch { /* non-fatal */ }
            await dialog.ShowDialog(this);
        }
    }

    private async void OnImportRequested()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var wizardVm = new ImportWizardViewModel(
            new DevolutionsImporterAdapter(),
            new RdpFileImporterAdapter(),
            new MRemoteNgImporterAdapter(),
            new MobaXtermImporterAdapter());

        var wizard = new ImportWizard { DataContext = wizardVm };

        wizardVm.CloseRequested += () => wizard.Close();
        wizardVm.EntriesImported += pairs =>
        {
            foreach (var (entry, group) in pairs)
                vm.ConnectionTree.AddEntry(entry, group);
        };

        try { wizard.Icon = AppIconGenerator.Create(); } catch { /* non-fatal */ }
        await wizard.ShowDialog(this);
    }

    private async void OnGitHubSignInRequested()
    {
        var dialog = new OAuthConnectDialog();
        var result = await dialog.ShowDialog<object?>(this);

        // Propagate the identity to SettingsViewModel so connected state reflects immediately
        var settingsVm = AppServices.GetRequired<SettingsViewModel>();
        switch (result)
        {
            case SpotDesk.Core.Auth.GitHubIdentity github:
                settingsVm.IsGitHubConnected = true;
                settingsVm.GithubLogin       = github.Login;
                break;
        }
    }

    private void OnSearchOpenRequested()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var searchBox = new SearchBox { DataContext = vm.Search };
        if (Content is Panel rootPanel)
            rootPanel.Children.Add(searchBox);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
            e.Handled = true;
        }
    }
}
