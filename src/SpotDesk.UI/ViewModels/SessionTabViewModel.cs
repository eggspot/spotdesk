using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpotDesk.Core.Models;
using SpotDesk.Protocols;

namespace SpotDesk.UI.ViewModels;

public partial class SessionTabViewModel : ObservableObject, IDisposable
{
    private const int ReconnectDelaySeconds = 3;

    public Guid ConnectionId { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private Protocol _protocol;
    [ObservableProperty] private SessionStatus _status = SessionStatus.Idle;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _latencyMs;
    [ObservableProperty] private string? _codec;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private int _reconnectCountdown;
    [ObservableProperty] private bool _isReconnecting;
    [ObservableProperty] private bool _hasFrame;

    private readonly ConnectionEntry _connection;
    private readonly ISessionManager? _sessionManager;
    private IRdpSession? _rdpSession;

    private System.Threading.Timer? _reconnectTimer;
    private CancellationTokenSource? _reconnectCts;

    // ── Derived display ───────────────────────────────────────────────────

    public string StatusColor => Status switch
    {
        SessionStatus.Connected                       => "#22C55E",
        SessionStatus.Connecting or
        SessionStatus.Reconnecting                    => "#F59E0B",
        SessionStatus.Error                           => "#EF4444",
        _                                             => "#6B7280"
    };

    public string ProtocolIcon => Protocol switch
    {
        Protocol.Rdp => "M 2 2 L 14 2 L 14 10 L 2 10 Z",
        Protocol.Ssh => "M 3 8 L 7 4 L 11 8",
        Protocol.Vnc => "M 2 2 L 14 14 M 14 2 L 2 14",
        _            => string.Empty
    };

    public bool IsConnecting => Status is SessionStatus.Connecting or SessionStatus.Reconnecting;
    public bool IsError      => Status == SessionStatus.Error;
    public bool IsIdle       => Status == SessionStatus.Idle;

    partial void OnStatusChanged(SessionStatus value)
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsIdle));
    }

    // ── SSH status bar properties ─────────────────────────────────────────
    public string StatusText  => StatusMessage ?? Status.ToString();
    public string LatencyText => LatencyMs > 0 ? $"{LatencyMs}ms" : string.Empty;

    // ── RDP/VNC frame bitmap (used by RdpView / VncView) ─────────────────

    public WriteableBitmap? CurrentFrame { get; private set; }

    public event Action<WriteableBitmap?>? FrameBitmapChanged;

    public void NotifyFrameBitmapChanged(WriteableBitmap? bitmap)
    {
        CurrentFrame = bitmap;
        HasFrame     = bitmap != null;
        FrameBitmapChanged?.Invoke(bitmap);
    }

    // ── SshTabViewModel compatibility (SshView reads this) ───────────────

    private SpotDesk.Protocols.Ssh.Terminal.TerminalBuffer? _terminalBuffer;
    public SpotDesk.Protocols.Ssh.Terminal.TerminalBuffer TerminalBuffer =>
        _terminalBuffer ??= new SpotDesk.Protocols.Ssh.Terminal.TerminalBuffer();

    // ── Construction ─────────────────────────────────────────────────────

    public SessionTabViewModel(ConnectionEntry connection, ISessionManager? sessionManager = null)
    {
        ConnectionId    = connection.Id;
        _displayName    = connection.Name;
        _protocol       = connection.Protocol;
        _statusMessage  = string.Empty;
        _connection     = connection;
        _sessionManager = sessionManager;
    }

    // ── Commands ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectAsync()
    {
        _reconnectCts?.Cancel();
        _reconnectTimer?.Dispose();
        IsReconnecting = false;
        HasFrame       = false;

        Status        = SessionStatus.Connecting;
        StatusMessage = "Connecting…";

        if (_sessionManager is null)
        {
            // Running without a real session manager (unit tests, design-time)
            await Task.Delay(200);
            Status        = SessionStatus.Error;
            StatusMessage = "No session manager available — running in UI preview mode";
            return;
        }

        try
        {
            var credential = new CredentialEntry(); // TODO: resolve from vault by connection.CredentialId
            _rdpSession = _sessionManager.GetOrCreateRdp(_connection, credential);

            _rdpSession.StatusChanged  += OnSessionStatusChanged;
            _rdpSession.LatencyUpdated += OnLatencyUpdated;
            _rdpSession.FrameUpdated   += OnFrameUpdated;

            await _rdpSession.ConnectAsync();
        }
        catch (Exception ex)
        {
            Status        = SessionStatus.Error;
            StatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    private void OnSessionStatusChanged(SessionStatus status) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Status        = status;
            StatusMessage = status switch
            {
                SessionStatus.Connected      => "Connected",
                SessionStatus.Connecting     => "Connecting…",
                SessionStatus.Reconnecting   => "Reconnecting…",
                SessionStatus.Disconnecting  => "Disconnecting…",
                SessionStatus.Idle           => string.Empty,
                _                            => StatusMessage
            };
        });

    private void OnLatencyUpdated(int latency) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LatencyMs = latency);

    private void OnFrameUpdated() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            NotifyFrameBitmapChanged(_rdpSession?.GetFrameBuffer()));

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        Status        = SessionStatus.Disconnecting;
        StatusMessage = "Disconnecting…";
        if (_rdpSession != null)
            await _rdpSession.DisconnectAsync();
        else
            await Task.CompletedTask;
        Status        = SessionStatus.Idle;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private Task ReconnectAsync() => ConnectAsync();

    [RelayCommand]
    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectTimer?.Dispose();
        IsReconnecting     = false;
        ReconnectCountdown = 0;
        Status             = SessionStatus.Idle;
        StatusMessage      = string.Empty;
    }

    /// <summary>Raised when the user clicks the tab close button. MainWindowViewModel handles removal.</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private void Close()
    {
        _reconnectCts?.Cancel();
        _reconnectTimer?.Dispose();
        IsReconnecting = false;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void FitWindow() { }

    [RelayCommand]
    private void TakeScreenshot()
    {
        if (CurrentFrame is null) return;

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads))
            downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var safe     = string.Concat(DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var filename = $"spotdesk-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        var path     = Path.Combine(downloads, filename);

        CurrentFrame.Save(path);
    }

    public event Action? CtrlAltDelRequested;

    [RelayCommand]
    private void SendCtrlAltDel() => CtrlAltDelRequested?.Invoke();

    public event Action<Avalonia.Input.KeyEventArgs>? KeyInputReceived;
    public void HandleKeyInput(Avalonia.Input.KeyEventArgs e) => KeyInputReceived?.Invoke(e);

    // ── Auto-reconnect ────────────────────────────────────────────────────

    public void OnUnexpectedDisconnect(string? reason = null)
    {
        Status        = SessionStatus.Error;
        StatusMessage = reason ?? "Connection lost";
        IsReconnecting     = true;
        ReconnectCountdown = ReconnectDelaySeconds;

        _reconnectCts = new CancellationTokenSource();
        var token     = _reconnectCts.Token;

        _reconnectTimer = new System.Threading.Timer(_ =>
        {
            if (token.IsCancellationRequested) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;

                ReconnectCountdown--;
                if (ReconnectCountdown <= 0)
                {
                    _reconnectTimer?.Dispose();
                    _ = ReconnectAsync();
                }
            });
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        _reconnectCts?.Cancel();
        _reconnectTimer?.Dispose();
        if (_rdpSession != null)
        {
            _rdpSession.StatusChanged  -= OnSessionStatusChanged;
            _rdpSession.LatencyUpdated -= OnLatencyUpdated;
            _rdpSession.FrameUpdated   -= OnFrameUpdated;
            _sessionManager?.Close(ConnectionId);
        }
        GC.SuppressFinalize(this);
    }
}
