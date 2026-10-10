using System.Text.Json;

namespace OpenMouseConfig.Core.Definitions;

public enum CatalogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A message produced while loading definition files.</summary>
public sealed record CatalogMessage(CatalogSeverity Severity, string Source, string Text)
{
    public override string ToString() => $"{Source}: {Text}";
}

/// <summary>
/// All device definitions currently known to the app, merged from several JSON files.
/// </summary>
/// <remarks>
/// Files are passed in increasing priority. Typically:
/// <list type="number">
///   <item><c>devices.json</c> next to the executable (bundled, replaced on every update)</item>
///   <item><c>%APPDATA%\OpenMouseConfig\devices\*.json</c> (user files, survive updates)</item>
/// </list>
/// A definition in a later file replaces an earlier one with the same <c>id</c>, and definitions
/// from later files are matched first — so a user can override the bundled G-Wolves entry or add
/// a brand-new mouse without touching the install directory and without recompiling.
/// A broken file or device is skipped and reported; it never prevents the others from loading.
/// </remarks>
public sealed class DeviceCatalog
{
    private DeviceCatalog(IReadOnlyList<DeviceDefinition> devices, IReadOnlyList<CatalogMessage> messages, IReadOnlyList<string> files)
    {
        Devices = devices;
        Messages = messages;
        Files = files;
    }

    public static DeviceCatalog Empty { get; } = new([], [], []);

    /// <summary>Valid definitions, highest priority first.</summary>
    public IReadOnlyList<DeviceDefinition> Devices { get; }

    public IReadOnlyList<CatalogMessage> Messages { get; }

    /// <summary>Files that were read successfully.</summary>
    public IReadOnlyList<string> Files { get; }

    public int ErrorCount => Messages.Count(m => m.Severity == CatalogSeverity.Error);

    /// <summary>Loads and merges the given files (lowest priority first). Missing files are skipped silently.</summary>
    public static DeviceCatalog Load(IEnumerable<string> filesInPriorityOrder)
    {
        var messages = new List<CatalogMessage>();
        var loadedFiles = new List<string>();
        var byId = new Dictionary<string, (DeviceDefinition Device, int FileRank, int Position)>(StringComparer.OrdinalIgnoreCase);

        int fileRank = 0;
        foreach (var path in filesInPriorityOrder)
        {
            fileRank++;
            if (!File.Exists(path))
            {
                continue;
            }

            var fileName = Path.GetFileName(path);
            DefinitionFile? file;
            try
            {
                file = JsonSerializer.Deserialize<DefinitionFile>(File.ReadAllText(path), DefinitionJson.Options);
            }
            catch (JsonException ex)
            {
                // ex.Message already contains "Path: $.devices[0]... | LineNumber: 12 | BytePositionInLine: 8".
                messages.Add(new(CatalogSeverity.Error, fileName, $"Invalid JSON — file skipped. {ex.Message}"));
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add(new(CatalogSeverity.Error, fileName, $"Cannot read file: {ex.Message}"));
                continue;
            }

            loadedFiles.Add(path);
            if (file?.Devices is null || file.Devices.Count == 0)
            {
                messages.Add(new(CatalogSeverity.Warning, fileName, "No \"devices\" found in file."));
                continue;
            }

            if (file.SchemaVersion > 1)
            {
                messages.Add(new(CatalogSeverity.Warning, fileName,
                    $"schemaVersion {file.SchemaVersion} is newer than this app understands (1); some settings may be ignored."));
            }

            for (int i = 0; i < file.Devices.Count; i++)
            {
                var device = file.Devices[i];
                if (device is null)
                {
                    continue;
                }

                var source = $"{fileName} › {(string.IsNullOrWhiteSpace(device.Id) ? $"devices[{i}]" : device.Id)}";
                var errors = new List<string>();
                var warnings = new List<string>();
                DefinitionValidator.Validate(device, errors, warnings);

                messages.AddRange(warnings.Select(w => new CatalogMessage(CatalogSeverity.Warning, source, w)));
                if (errors.Count > 0)
                {
                    messages.AddRange(errors.Select(e => new CatalogMessage(CatalogSeverity.Error, source, e)));
                    messages.Add(new(CatalogSeverity.Error, source, "Device skipped because of the errors above."));
                    continue;
                }

                device.SourceFile = path;
                if (byId.TryGetValue(device.Id, out var existing))
                {
                    messages.Add(new(CatalogSeverity.Info, source,
                        $"Overrides the definition from {Path.GetFileName(existing.Device.SourceFile)}."));
                }

                byId[device.Id] = (device, fileRank, i);
            }
        }

        var ordered = byId.Values
            .OrderByDescending(e => e.FileRank)
            .ThenBy(e => e.Position)
            .Select(e => e.Device)
            .ToList();

        return new DeviceCatalog(ordered, messages, loadedFiles);
    }
}
