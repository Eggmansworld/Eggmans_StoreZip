using System.Text.Json;

namespace StoreZip.App;

/// <summary>Remembered between runs, in the user's own settings folder (works on every OS, and inside a macOS app bundle).</summary>
public sealed class Settings
{
    public string OutputFolder { get; set; } = "";
    public bool RenameExtension { get; set; } = true;

    private static string PathOf => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EggmansStoreZip", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(PathOf))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new Settings();
        }
        catch
        {
            // A damaged settings file only means the defaults
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Not being able to remember settings never stops a conversion
        }
    }
}
