using System.IO;
using System.Text.Json;
using OpenMouseConfig.Core.Devices;

namespace OpenMouseConfig.Services;

/// <summary>Persisted UI state.</summary>
public sealed class AppSettings
{
    /// <summary>Definition id of the last used mouse, re-selected on startup.</summary>
    public string? LastDefinitionId { get; set; }

    public bool ShowOfflineDevices { get; set; }

    public bool ConsoleExpanded { get; set; }

    /// <summary>
    /// Last values successfully applied per definition id. Shown on reconnect for mice that can't
    /// report their configuration (no read-back command), so the UI doesn't lie with defaults.
    /// </summary>
    public Dictionary<string, MouseSettings> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON. Never throws: a corrupt file just resets settings.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(path))
            {
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
                Settings.Profiles = new Dictionary<string, MouseSettings>(Settings.Profiles ?? [], StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temp, path, overwrite: true); // atomic replace: no half-written settings on crash
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save them must not break the app.
        }
    }

    public MouseSettings? GetProfile(string definitionId) =>
        Settings.Profiles.TryGetValue(definitionId, out var profile) ? profile.Clone() : null;

    public void SetProfile(string definitionId, MouseSettings settings) =>
        Settings.Profiles[definitionId] = settings.Clone();
}
