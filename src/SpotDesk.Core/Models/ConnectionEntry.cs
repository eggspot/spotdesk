namespace SpotDesk.Core.Models;

public record ConnectionEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public Protocol Protocol { get; set; }
    public Guid? CredentialId { get; set; }
    public Guid? GroupId { get; set; }
    public string[] Tags { get; set; } = [];
    public bool IsFavorite { get; set; }
    public bool RememberPassword { get; set; } = false;
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Advanced RDP settings
    public int? DesktopWidth { get; set; }
    public int? DesktopHeight { get; set; }
    public int ColorDepth { get; set; } = 32;
    public bool EnableRemoteFx { get; set; } = true;
    public bool EnableDriveRedirection { get; set; }

    public static int DefaultPortFor(Protocol protocol) => protocol switch
    {
        Protocol.Rdp => 3389,
        Protocol.Ssh => 22,
        Protocol.Vnc => 5900,
        _ => 0
    };
}

public enum Protocol { Rdp, Ssh, Vnc }
