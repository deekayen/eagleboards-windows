using System.IO;
using System.Text.Json;

namespace EagleBoards.App;

/// <summary>
/// What the start-up window remembers between runs, kept per Windows user in
/// %LOCALAPPDATA%\EagleBoards\settings.json. Holds paths and network
/// choices only -- never the SignUpGenius key and never participant data.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// The folder that holds config.properties, Master_AdultHistory.csv and
    /// one dated sub-folder per event night (e.g. C:\eagleboards).
    /// </summary>
    public string DataFolder { get; set; } = "";

    /// <summary>File name or full path of the cumulative adult history.</summary>
    public string AdultHistoryFile { get; set; } = "Master_AdultHistory.csv";

    public int Port { get; set; } = 8080;

    /// <summary>IPv4 address to serve on, or empty for every network.</summary>
    public string BindAddress { get; set; } = "";

    public bool ImportSignUpGenius { get; set; } = true;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EagleBoards", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file only costs the remembered choices.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The SignUpGenius key: the SUG_KEY environment variable, else a
    /// <c>SUG_KEY=</c> line in a .env file in the data folder or beside the
    /// exe -- the same places the Java launcher looked. Returns the key and
    /// where it came from, or nulls.
    /// </summary>
    public static (string? Key, string? Source) FindSignUpGeniusKey(string dataFolder)
    {
        var env = Environment.GetEnvironmentVariable("SUG_KEY");
        if (IsRealKey(env))
        {
            return (env, "the SUG_KEY environment variable");
        }

        foreach (var dir in new[] { dataFolder, AppContext.BaseDirectory }.Where(d => d.Length > 0))
        {
            var path = Path.Combine(dir, ".env");
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("SUG_KEY=", StringComparison.Ordinal))
                    {
                        var key = trimmed["SUG_KEY=".Length..].Trim().Trim('"', '\'');
                        if (IsRealKey(key))
                        {
                            return (key, path);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (null, null);
    }

    private static bool IsRealKey(string? key) => key is { Length: > 10 } && key != "replace-with-real-key";
}
