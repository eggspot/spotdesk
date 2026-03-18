using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotDesk.UI.ViewModels;

/// <summary>
/// Persists lightweight UI preferences to ~/.config/spotdesk/prefs.json.
/// NativeAOT-safe via source-generated JsonSerializerContext.
/// </summary>
public class LocalPrefsService
{
    private static readonly string PrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "spotdesk",
        "prefs.json");

    public LocalPrefs Load()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return new LocalPrefs();
            var json = File.ReadAllText(PrefsPath);
            return JsonSerializer.Deserialize(json, LocalPrefsJsonContext.Default.LocalPrefs)
                   ?? new LocalPrefs();
        }
        catch
        {
            return new LocalPrefs();
        }
    }

    public void Save(LocalPrefs prefs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            var json = JsonSerializer.Serialize(prefs, LocalPrefsJsonContext.Default.LocalPrefs);
            File.WriteAllText(PrefsPath, json);
        }
        catch
        {
            // Prefs are best-effort — never crash on write failure
        }
    }

    public void Save(Func<LocalPrefs, LocalPrefs> update) => Save(update(Load()));
}

public record LocalPrefs
{
    [JsonPropertyName("theme")]
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    [JsonPropertyName("sidebarVisible")]
    public bool SidebarVisible { get; init; } = true;

    [JsonPropertyName("sidebarWidth")]
    public double SidebarWidth { get; init; } = 240;

    [JsonPropertyName("sidebarPinned")]
    public bool SidebarPinned { get; init; } = true;

    [JsonPropertyName("terminalFontSize")]
    public int TerminalFontSize { get; init; } = 13;

    [JsonPropertyName("lastActiveGroup")]
    public string? LastActiveGroup { get; init; }

    [JsonPropertyName("vaultRepoPath")]
    public string? VaultRepoPath { get; init; }

    /// <summary>"github" (default, requires sign-in + Git repo) or "local" (password-only, no sync).</summary>
    [JsonPropertyName("vaultMode")]
    public string VaultMode { get; init; } = "github";

    [JsonPropertyName("autoSyncInterval")]
    public string? AutoSyncInterval { get; init; }

    [JsonPropertyName("tabOrder")]
    public List<Guid> TabOrder { get; init; } = [];
}

[JsonSerializable(typeof(LocalPrefs))]
[JsonSerializable(typeof(List<Guid>))]
public partial class LocalPrefsJsonContext : JsonSerializerContext;
