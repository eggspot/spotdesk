using System.Xml.Linq;
using SpotDesk.Core.Models;

namespace SpotDesk.Core.Import;

/// <summary>
/// Parses mRemoteNG connection files (confCons.xml / *.xml).
/// Format: XML with &lt;mrng:Connections&gt; root (namespace http://mremoteng.org)
/// and &lt;Node Type="Connection"&gt; / &lt;Node Type="Container"&gt; elements.
/// Passwords are encrypted with a user-supplied key and are not imported.
/// </summary>
public class MRemoteNgImporter
{
    public ImportResult Import(Stream stream)
    {
        var connections = new List<ConnectionEntry>();
        var warnings    = new List<string>();
        var errors      = new List<string>();

        try
        {
            var doc = XDocument.Load(stream);

            // Walk the tree recursively, tracking the immediate parent container name as group
            foreach (var node in doc.Root?.Elements() ?? Enumerable.Empty<XElement>())
                WalkNode(node, parentGroup: null, connections, warnings);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse mRemoteNG file: {ex.Message}");
        }

        return new ImportResult
        {
            Connections = [.. connections],
            Credentials = [],
            Warnings    = [.. warnings],
            Errors      = [.. errors]
        };
    }

    private static void WalkNode(
        XElement node,
        string?  parentGroup,
        List<ConnectionEntry> connections,
        List<string> warnings)
    {
        var type = node.Attribute("Type")?.Value;

        if (type == "Container")
        {
            // Use this container's name as the group for its direct children
            var groupName = node.Attribute("Name")?.Value ?? parentGroup;
            foreach (var child in node.Elements())
                WalkNode(child, groupName, connections, warnings);
            return;
        }

        if (type != "Connection") return;

        var conn = ParseNode(node, parentGroup, warnings);
        if (conn is not null) connections.Add(conn);
    }

    private static ConnectionEntry? ParseNode(
        XElement node,
        string?  group,
        List<string> warnings)
    {
        var name     = node.Attribute("Name")?.Value ?? "Unnamed";
        var hostname = node.Attribute("Hostname")?.Value ?? string.Empty;
        var proto    = node.Attribute("Protocol")?.Value ?? string.Empty;
        var portStr  = node.Attribute("Port")?.Value;
        var username = node.Attribute("Username")?.Value ?? string.Empty;

        if (string.IsNullOrWhiteSpace(hostname))
        {
            warnings.Add($"Skipped '{name}': no hostname.");
            return null;
        }

        var protocol = proto.ToUpperInvariant() switch
        {
            "RDP"   => Protocol.Rdp,
            "SSH2"  => Protocol.Ssh,
            "SSH1"  => Protocol.Ssh,
            "VNC"   => Protocol.Vnc,
            "TELNET"=> Protocol.Ssh,   // closest available; renders as SSH tab
            _       => (Protocol?)null
        };

        if (protocol is null)
        {
            // HTTP, HTTPS, ICA, IntApp, etc. — not supported, skip silently
            return null;
        }

        var port = portStr is not null && int.TryParse(portStr, out var p)
            ? p
            : ConnectionEntry.DefaultPortFor(protocol.Value);

        return new ConnectionEntry
        {
            Name     = name,
            Host     = hostname,
            Port     = port,
            Protocol = protocol.Value,
            Tags     = group is not null ? [group] : []
        };
    }

    /// <summary>
    /// Returns true if the stream looks like an mRemoteNG file
    /// (contains the mremoteng.org namespace URI or ConfVersion attribute near the top).
    /// Does not consume the stream.
    /// </summary>
    public static bool Detect(Stream stream)
    {
        Span<byte> buf = stackalloc byte[1024];
        var read = stream.Read(buf);
        if (stream.CanSeek) stream.Position = 0;

        var snippet = System.Text.Encoding.UTF8.GetString(buf[..read]);
        return snippet.Contains("mremoteng.org", StringComparison.OrdinalIgnoreCase)
            || snippet.Contains("ConfVersion", StringComparison.Ordinal);
    }
}
