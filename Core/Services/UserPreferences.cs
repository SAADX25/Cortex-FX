using System.IO;
using System.Text.Json;

namespace CortexFX.Core.Services;

/// <summary>
/// Lightweight persisted user preferences (LocalAppData\CortexFX\userprefs.json).
/// </summary>
public sealed class UserPreferences
{
    private static readonly object Sync = new();
    private static UserPreferences? _current;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static UserPreferences Current
    {
        get
        {
            lock (Sync)
            {
                return _current ??= Load();
            }
        }
    }

    /// <summary>Audio Cutter volume preference (0–2). Applied to preview and export.</summary>
    public double AudioVolume { get; set; } = 1.0;

    public void Save()
    {
        lock (Sync)
        {
            try
            {
                AudioVolume = Math.Clamp(AudioVolume, 0, 2);
                string path = GetPrefsPath();
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                // Preferences are best-effort.
            }
        }
    }

    private static UserPreferences Load()
    {
        try
        {
            string path = GetPrefsPath();
            if (File.Exists(path))
            {
                var prefs = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path));
                if (prefs != null)
                {
                    prefs.AudioVolume = Math.Clamp(prefs.AudioVolume, 0, 2);
                    return prefs;
                }
            }
        }
        catch
        {
            // Fall through to defaults.
        }

        return new UserPreferences();
    }

    public static string GetPrefsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CortexFX",
            "userprefs.json");
}
