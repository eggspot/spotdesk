using SpotDesk.Core.Models;

namespace SpotDesk.Core.Import;

/// <summary>
/// Parses MobaXterm session export files (.mxtsessions).
/// Format: INI-like text file. Each session is a key=value line under [Bookmarks] or [Bookmarks_X].
///
/// Line format:
///   DisplayName=#SessionType#Arg1#Arg2#...#
///
/// SessionType codes (subset we care about):
///   0 = SSH      → Arg1=Host, Arg2=Username, Arg3=Port
///   4 = RDP      → Arg1=Host, Arg2=Username, Arg3=Port
///   91 = VNC     → Arg1=Host, Arg2=Port
///
/// Example:
///   [Bookmarks]
///   SubRep=
///   ImgNum=42
///   My Server=#0#192.168.1.10#root#22#...
///   Win Box=#4#10.0.0.5#Administrator#3389#...
/// </summary>
public class MobaXtermImporter
{
    private const int SshType = 0;
    private const int RdpType = 4;
    private const int VncType = 91;

    public ImportResult Import(Stream stream)
    {
        var connections = new List<ConnectionEntry>();
        var warnings    = new List<string>();
        var errors      = new List<string>();

        try
        {
            using var reader = new StreamReader(stream);
            string? currentSection = null;

            while (reader.ReadLine() is { } rawLine)
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentSection = line[1..^1];
                    continue;
                }

                // Only process lines in [Bookmarks*] sections
                if (currentSection is null ||
                    !currentSection.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip metadata lines
                if (line.StartsWith("SubRep=", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("ImgNum=",  StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    continue;

                var eq = line.IndexOf('=');
                if (eq < 0) continue;

                var displayName = line[..eq].Trim();
                var value       = line[(eq + 1)..].Trim();

                var conn = ParseSessionLine(displayName, value, warnings);
                if (conn is not null) connections.Add(conn);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse MobaXterm file: {ex.Message}");
        }

        return new ImportResult
        {
            Connections = [.. connections],
            Credentials = [],
            Warnings    = [.. warnings],
            Errors      = [.. errors]
        };
    }

    private static ConnectionEntry? ParseSessionLine(
        string displayName, string value, List<string> warnings)
    {
        // Value format: #TypeCode#Arg0#Arg1#Arg2#...#
        if (!value.StartsWith('#')) return null;

        var parts = value.Split('#', StringSplitOptions.None);
        // parts[0] = "" (before first #), parts[1] = TypeCode, parts[2..] = args
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[1], out var sessionType)) return null;

        var protocol = sessionType switch
        {
            SshType => Protocol.Ssh,
            RdpType => Protocol.Rdp,
            VncType => Protocol.Vnc,
            _       => (Protocol?)null
        };

        if (protocol is null) return null;

        // Arg layout per type:
        //   SSH (0): #0#host#username#port#...
        //   RDP (4): #4#host#username#port#...
        //   VNC(91): #91#host#port#...
        string host     = parts.Length > 2 ? parts[2] : string.Empty;
        string username = string.Empty;
        int    port     = ConnectionEntry.DefaultPortFor(protocol.Value);

        if (sessionType == VncType)
        {
            if (parts.Length > 3 && int.TryParse(parts[3], out var vp)) port = vp;
        }
        else
        {
            if (parts.Length > 3) username = parts[3];
            if (parts.Length > 4 && int.TryParse(parts[4], out var p)) port = p;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            warnings.Add($"Skipped '{displayName}': no hostname.");
            return null;
        }

        return new ConnectionEntry
        {
            Name     = displayName,
            Host     = host,
            Port     = port,
            Protocol = protocol.Value,
            Tags     = []
        };
    }
}
