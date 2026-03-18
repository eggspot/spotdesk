using Avalonia.Controls;
using Avalonia.Threading;
using SpotDesk.UI.ViewModels;

namespace SpotDesk.UI.Views;

/// <summary>
/// RDP session view.
/// Renders the session framebuffer into an Image control defined in XAML.
/// The session toolbar auto-hides after 2 seconds on pointer leave.
/// </summary>
public partial class RdpView : UserControl
{
    private DispatcherTimer? _toolbarHideTimer;

    public RdpView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var toolbar = this.FindControl<Border>("SessionToolbar");
        var image   = this.FindControl<Image>("SessionImage");

        _toolbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _toolbarHideTimer.Tick += (_, _) =>
        {
            _toolbarHideTimer.Stop();
            if (toolbar is not null) toolbar.IsVisible = false;
        };

        // Show toolbar on pointer move over the session area
        PointerMoved += (_, _) =>
        {
            if (toolbar is not null) toolbar.IsVisible = true;
            _toolbarHideTimer?.Stop();
            _toolbarHideTimer?.Start();
        };
        PointerExited += (_, _) =>
        {
            _toolbarHideTimer?.Stop();
            _toolbarHideTimer?.Start();
        };

        // Wire toolbar buttons
        var btnFit        = this.FindControl<Button>("BtnFitWindow");
        var btnFull       = this.FindControl<Button>("BtnFullScreen");
        var btnScreenshot = this.FindControl<Button>("BtnScreenshot");
        var btnCad        = this.FindControl<Button>("BtnCtrlAltDel");

        if (btnFit        is not null) btnFit.Click        += (_, _) => (DataContext as SessionTabViewModel)?.FitWindowCommand.Execute(null);
        if (btnFull       is not null) btnFull.Click       += (_, _) => ToggleFullScreen();
        if (btnScreenshot is not null) btnScreenshot.Click += (_, _) => (DataContext as SessionTabViewModel)?.TakeScreenshotCommand.Execute(null);
        if (btnCad        is not null) btnCad.Click        += (_, _) => (DataContext as SessionTabViewModel)?.SendCtrlAltDelCommand.Execute(null);

        // Wire framebuffer updates from the session backend to the Image control
        if (image is not null && DataContext is SessionTabViewModel vm)
            vm.FrameBitmapChanged += bitmap => Dispatcher.UIThread.Post(() => image.Source = bitmap);
    }

    private void ToggleFullScreen()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window is null) return;
        window.WindowState = window.WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
    }
}
