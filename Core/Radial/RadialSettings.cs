using System.IO;
using System.Text.Json;

namespace CortexFX.Core.Radial;

public sealed class RadialSettings
{
    public bool Enabled { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public int Quality { get; set; } = 85;
    public int PdfDpi { get; set; } = 200;
    public string ConvertKey { get; set; } = "Shift";
    public string ToolsKey { get; set; } = "Alt";
    public string QuickKey { get; set; } = "Ctrl";
    public string FFmpegPath { get; set; } = "";

    public void Validate()
    {
        if (Quality is < 10 or > 100 || PdfDpi is < 72 or > 600)
            throw new ArgumentException("Choose quality 10–100 and PDF resolution 72–600 DPI.");
        var keys = new HashSet<string> { ConvertKey, ToolsKey, QuickKey };
        if (!keys.SetEquals(new[] { "Shift", "Alt", "Ctrl" }))
            throw new ArgumentException("Assign a different modifier to each wheel.");
        if (FFmpegPath is null) throw new ArgumentException("Invalid FFmpeg path.");
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CortexFX", "radial-wheel.json");

    public static RadialSettings Load(string? path = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<RadialSettings>(File.ReadAllText(path ?? DefaultPath)) ?? new();
            settings.Validate();
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new();
        }
    }

    public void Save(string? path = null)
    {
        Validate();
        string destination = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
    }
}
