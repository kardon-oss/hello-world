using System.IO;

namespace OpenMouseConfig.Services;

/// <summary>
/// Where the app reads definitions and stores settings.
/// <list type="bullet">
///   <item><c>{install dir}\devices.json</c> — bundled definitions (overwritten by updates)</item>
///   <item><c>%APPDATA%\OpenMouseConfig\devices\*.json</c> — user definitions (override bundled ones by id)</item>
///   <item><c>%APPDATA%\OpenMouseConfig\settings.json</c> — UI state and last-applied values per mouse</item>
/// </list>
/// </summary>
public sealed class AppPaths
{
    public string BundledDefinitionsFile { get; } = Path.Combine(AppContext.BaseDirectory, "devices.json");

    public string UserDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenMouseConfig");

    public string UserDefinitionsDirectory => Path.Combine(UserDataDirectory, "devices");

    public string SettingsFile => Path.Combine(UserDataDirectory, "settings.json");

    /// <summary>All definition files, lowest priority first (bundled, then user files alphabetically).</summary>
    public IReadOnlyList<string> GetDefinitionFiles()
    {
        var files = new List<string> { BundledDefinitionsFile };
        if (Directory.Exists(UserDefinitionsDirectory))
        {
            files.AddRange(Directory.GetFiles(UserDefinitionsDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase));
        }

        return files;
    }

    /// <summary>Creates the user folders and drops a short README so people know what the folder is for.</summary>
    public void EnsureUserFolders()
    {
        Directory.CreateDirectory(UserDefinitionsDirectory);

        var readme = Path.Combine(UserDefinitionsDirectory, "README.txt");
        if (!File.Exists(readme))
        {
            File.WriteAllText(readme, """
                OpenMouseConfig — user device definitions
                =========================================

                Every *.json file in this folder is loaded after the bundled devices.json
                (next to OpenMouseConfig.exe) and uses exactly the same format:

                    { "schemaVersion": 1, "devices": [ { "id": "...", ... } ] }

                * A definition here with the same "id" as a bundled one replaces it.
                * Files here survive app updates; the bundled devices.json does not.
                * After editing, click "Reload definitions" in the app's status bar.

                Tip: copy the bundled devices.json here as a starting point.
                Full reference: README.md in the project repository.
                """);
        }
    }
}
