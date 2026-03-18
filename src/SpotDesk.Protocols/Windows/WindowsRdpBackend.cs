using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SpotDesk.Core.Models;

namespace SpotDesk.Protocols.Windows;

/// <summary>
/// Windows-only RDP backend using AxMSTscLib COM interop.
/// Compiled only when TargetFramework is net10.0-windows.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsRdpBackend : IRdpBackend
{
    public IRdpSession CreateSession(ConnectionEntry connection, CredentialEntry credential) =>
        new WindowsRdpSession(connection, credential);
}

[SupportedOSPlatform("windows")]
public class WindowsRdpSession : IRdpSession
{
    private readonly ConnectionEntry _connection;
    private readonly CredentialEntry _credential;

    public Guid Id { get; } = Guid.NewGuid();
    public SessionStatus Status { get; private set; } = SessionStatus.Idle;
    public int LatencyMs { get; private set; }
    public string? Codec { get; private set; }

    public event Action<SessionStatus> StatusChanged = delegate { };
    public event Action<int> LatencyUpdated = delegate { };
    public event Action FrameUpdated = delegate { };

    public WindowsRdpSession(ConnectionEntry connection, CredentialEntry credential)
    {
        _connection = connection;
        _credential = credential;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetStatus(SessionStatus.Connecting);

        // AxMSTscLib COM interop is used here via NativeControlHost in RdpView.axaml.
        // The actual COM object lives in a WinForms-hosted control; this class
        // marshals events and input between Avalonia and the COM ActiveX control.
        //
        // TODO: Implement AxMSTscLib COM interop
        // Reference: https://learn.microsoft.com/en-us/windows/win32/termserv/remote-desktop-protocol

        await Task.Delay(100, ct); // Placeholder
        SetStatus(SessionStatus.Connected);
    }

    public Task DisconnectAsync()
    {
        SetStatus(SessionStatus.Disconnecting);
        SetStatus(SessionStatus.Idle);
        return Task.CompletedTask;
    }

    public WriteableBitmap? GetFrameBuffer() => null; // populated by AxMSTscLib COM once implemented

    public void SendKeyDown(int scanCode, bool isExtended = false) { }
    public void SendKeyUp(int scanCode, bool isExtended = false) { }
    public void SendMouseMove(int x, int y) { }
    public void SendMouseButton(MouseButton button, bool isDown) { }
    public void SendCtrlAltDel() { }

    public void Resize(int width, int height)
    {
        // UpdateSessionDisplaySettings via IMsTscAdvancedSettings
    }

    private void SetStatus(SessionStatus s) { Status = s; StatusChanged(s); }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
