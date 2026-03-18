using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SpotDesk.Core.Models;

namespace SpotDesk.Core.Import;

/// <summary>
/// Parses connection files exported from Devolutions Remote Desktop Manager.
/// Supports both XML (.rdm/.rdf) and JSON (.json) export formats.
/// </summary>
public class DevolutionsImporter
{
    // RDM ConnectionType codes
    private const int RdpType        = 1;
    private const int SshTerminal    = 77;  // SSH Terminal (most common in JSON exports)
    private const int SshPutty       = 8;   // PuTTY session
    private const int SshLegacy      = 66;  // Older SSH type
    private const int VncType        = 12;
    private const int GroupFolder    = 25;  // Group/folder — not a real connection

    public ImportResult Import(Stream stream, string? rdmMasterKey = null)
    {
        // Peek at first non-whitespace byte to decide XML vs JSON
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        var firstChar = PeekFirstChar(ms);
        ms.Position = 0;

        return firstChar == '{'
            ? ImportJson(ms)
            : ImportXml(ms, rdmMasterKey);
    }

    private static char PeekFirstChar(Stream s)
    {
        int b;
        while ((b = s.ReadByte()) != -1)
        {
            if (!char.IsWhiteSpace((char)b)) return (char)b;
        }
        return '\0';
    }

    // ── JSON format (.json export from RDM) ───────────────────────────────

    private static ImportResult ImportJson(Stream stream)
    {
        var connections = new List<ConnectionEntry>();
        var warnings    = new List<string>();
        var errors      = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            JsonElement connectionsArray;
            if (root.TryGetProperty("Connections", out connectionsArray) ||
                root.TryGetProperty("connections", out connectionsArray))
            {
                foreach (var entry in connectionsArray.EnumerateArray())
                {
                    try
                    {
                        var conn = ParseJsonConnection(entry, warnings);
                        if (conn is not null) connections.Add(conn);
                    }
                    catch (Exception ex)
                    {
                        var name = entry.TryGetProperty("Name", out var n) ? n.GetString() : "?";
                        warnings.Add($"Skipped '{name}': {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse JSON file: {ex.Message}");
        }

        return new ImportResult
        {
            Connections = [.. connections],
            Credentials = [],
            Warnings    = [.. warnings],
            Errors      = [.. errors]
        };
    }

    private static ConnectionEntry? ParseJsonConnection(JsonElement entry, List<string> warnings)
    {
        var type = entry.TryGetProperty("ConnectionType", out var ct) ? ct.GetInt32() : -1;

        // Skip group/folder entries — they hold no host
        if (type == GroupFolder) return null;

        var protocol = type switch
        {
            RdpType     => Protocol.Rdp,
            SshTerminal => Protocol.Ssh,
            SshPutty    => Protocol.Ssh,
            SshLegacy   => Protocol.Ssh,
            VncType     => Protocol.Vnc,
            _           => (Protocol?)null
        };

        if (protocol is null)
        {
            // Silently skip unknown types (credentials, VPN configs, etc.)
            return null;
        }

        var name = entry.TryGetProperty("Name", out var n) ? n.GetString() ?? "Unnamed" : "Unnamed";

        // Host: for RDP it's in "Url"; for SSH it's in Terminal.Host
        string host = string.Empty;
        if (entry.TryGetProperty("Url", out var url) && url.ValueKind == JsonValueKind.String)
            host = url.GetString() ?? string.Empty;

        if (string.IsNullOrEmpty(host) && entry.TryGetProperty("Terminal", out var terminal))
        {
            if (terminal.TryGetProperty("Host", out var th))
                host = th.GetString() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(host) && entry.TryGetProperty("Putty", out var putty))
        {
            if (putty.TryGetProperty("SessionHost", out var sh))
                host = sh.GetString() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(host)) return null;  // No host = nothing useful to import

        // Username (best-effort, no password — SafePassword is encrypted with user's key)
        string username = string.Empty;
        if (entry.TryGetProperty("RDP", out var rdp) && rdp.TryGetProperty("UserName", out var ru))
            username = ru.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(username) && entry.TryGetProperty("Terminal", out var t2))
            if (t2.TryGetProperty("Username", out var tu)) username = tu.GetString() ?? string.Empty;

        // Group: preserve full folder path, converting backslash to forward slash
        string? group = null;
        if (entry.TryGetProperty("Group", out var g))
        {
            var raw = g.GetString();
            if (raw is not null)
                group = raw.Replace('\\', '/').Trim('/');
        }

        return new ConnectionEntry
        {
            Name     = name,
            Host     = host,
            Port     = ConnectionEntry.DefaultPortFor(protocol.Value),
            Protocol = protocol.Value,
            Tags     = group is not null ? [group] : []
        };
    }

    // ── XML format (.rdm / .rdf export from RDM) ─────────────────────────

    private static ImportResult ImportXml(Stream stream, string? rdmMasterKey)
    {
        var connections = new List<ConnectionEntry>();
        var credentials = new List<CredentialEntry>();
        var warnings = new List<string>();
        var errors = new List<string>();

        try
        {
            Stream xmlStream = rdmMasterKey is not null
                ? DecryptRdmStream(stream, rdmMasterKey)
                : stream;

            var doc = XDocument.Load(xmlStream);

            // RDM exports use different element names depending on version/export type:
            // v1: <Connection> (older data sources)
            // v2: <ConnectionExportSerializationWrapper> (current RDM default export)
            // Also handle <data> elements used in some bulk exports.
            var entries = doc
                .Descendants()
                .Where(e => e.Name.LocalName is "Connection"
                                            or "ConnectionExportSerializationWrapper"
                                            or "data")
                // Skip wrapper/container elements — only leaf-level connection objects
                // have a ConnectionType child element.
                .Where(e => e.Element("ConnectionType") != null ||
                            e.Elements().Any(c => string.Equals(c.Name.LocalName, "connectionType",
                                                                 StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var entry in entries)
            {
                try
                {
                    var (conn, cred) = ParseXmlConnection(entry, warnings);
                    if (conn is not null) connections.Add(conn);
                    if (cred is not null) credentials.Add(cred);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Skipped entry '{GetField(entry, "Name")}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse .rdm file: {ex.Message}");
        }

        return new ImportResult
        {
            Connections = [.. connections],
            Credentials = [.. credentials],
            Warnings = [.. warnings],
            Errors = [.. errors]
        };
    }

    /// <summary>Case-insensitive field reader — handles both PascalCase and camelCase RDM variants.</summary>
    private static string? GetField(XElement entry, string name) =>
        entry.Elements()
             .FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
             ?.Value;

    private static (ConnectionEntry? Connection, CredentialEntry? Credential) ParseXmlConnection(
        XElement entry, List<string> warnings)
    {
        var typeStr = GetField(entry, "ConnectionType");
        if (!int.TryParse(typeStr, out var type)) return (null, null);

        if (type == GroupFolder) return (null, null);

        var protocol = type switch
        {
            RdpType     => Protocol.Rdp,
            SshTerminal => Protocol.Ssh,
            SshPutty    => Protocol.Ssh,
            SshLegacy   => Protocol.Ssh,
            VncType     => Protocol.Vnc,
            _           => (Protocol?)null
        };

        if (protocol is null)
        {
            warnings.Add($"Unknown connection type {type}, skipping.");
            return (null, null);
        }

        var name      = GetField(entry, "Name") ?? "Unnamed";
        var host      = GetField(entry, "Host") ?? string.Empty;
        var portStr   = GetField(entry, "Port");
        var username  = GetField(entry, "UserName") ?? GetField(entry, "Username") ?? string.Empty;
        var passwordRaw = GetField(entry, "Password");
        // RDM group path uses backslash-separated hierarchy — preserve full path as forward-slash
        var groupRaw  = GetField(entry, "Group");
        var group     = groupRaw is not null
            ? groupRaw.Replace('\\', '/').Trim('/')
            : null;
        var tagsStr   = GetField(entry, "Tags");

        var port = portStr is not null && int.TryParse(portStr, out var p)
            ? p
            : ConnectionEntry.DefaultPortFor(protocol.Value);

        // RDM may base64-encode passwords
        var password = TryDecodeBase64(passwordRaw);

        var credId = Guid.NewGuid();
        CredentialEntry? cred = null;

        // RDM passwords are AES-encrypted with the user's master key — skip them.
        // Only import username (plain text) when present.
        if (!string.IsNullOrEmpty(username))
        {
            cred = new CredentialEntry
            {
                Id       = credId,
                Name     = $"{name} credential",
                Username = username,
            };
        }

        // Prefer explicit Tags field; fall back to the group path
        string[] tags = tagsStr?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        ?? (group is not null ? [group] : []);

        var conn = new ConnectionEntry
        {
            Name = name,
            Host = host,
            Port = port,
            Protocol = protocol.Value,
            CredentialId = cred?.Id,
            Tags = tags
        };

        return (conn, cred);
    }

    private static string? TryDecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            // Sanity check: decoded text should be printable
            return decoded.All(c => !char.IsControl(c) || c == '\r' || c == '\n') ? decoded : value;
        }
        catch
        {
            return value;
        }
    }

    private static Stream DecryptRdmStream(Stream encrypted, string masterKey)
    {
        // RDM uses AES-CBC with a key derived from the master password
        // This is a stub — real implementation depends on RDM's specific scheme
        throw new NotImplementedException("Encrypted .rdm decryption not yet implemented.");
    }
}
