namespace CortexFX.Core.Constants;

/// <summary>
/// Single source of truth for all supported file extensions and their categories.
/// Every extension check in the codebase must reference these sets — no inline string arrays.
/// </summary>
public static class MediaTypes
{
    // --- Documents ---
    public static readonly IReadOnlySet<string> DocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls"
    };

    public static readonly IReadOnlySet<string> PdfExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public static readonly IReadOnlySet<string> WordExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc"
    };

    public static readonly IReadOnlySet<string> PowerPointExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".ppt"
    };

    public static readonly IReadOnlySet<string> ExcelExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xls"
    };

    // --- Images ---
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".ico"
    };

    public static readonly IReadOnlySet<string> RasterImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    // --- Audio ---
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg"
    };

    /// <summary>Audio formats that trigger the Audio Editor choice overlay.</summary>
    public static readonly IReadOnlySet<string> AudioEditorExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".ogg"
    };

    // --- Video ---
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".avi", ".mov", ".mkv", ".webm"
    };

    // --- FFmpeg-handled output formats ---
    public static readonly IReadOnlySet<string> FFmpegOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mp3", "avi", "wav", "mkv", "mov", "gif", "webm", "m4a", "ogg", "flac", "aac"
    };

    /// <summary>FFmpeg audio-only extraction formats (use -vn flag).</summary>
    public static readonly IReadOnlySet<string> AudioOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "aac", "flac", "m4a"
    };

    // --- Magick-handled output formats ---
    public static readonly IReadOnlySet<string> MagickOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "bmp", "webp", "ico"
    };

    // --- All supported extensions (union for global validation) ---
    public static readonly IReadOnlySet<string> AllSupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls",
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".ico",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg",
        ".mp4", ".avi", ".mov", ".mkv", ".webm"
    };

    // --- Conversion Rules (input extension → available target formats) ---
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ConversionRules =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Documents
            { ".pdf",  new[] { "DOCX", "PPTX", "XLSX", "JPG", "PNG" } },
            { ".docx", new[] { "PDF", "PPTX" } },
            { ".doc",  new[] { "PDF", "PPTX" } },
            { ".pptx", new[] { "PDF", "DOCX" } },
            { ".ppt",  new[] { "PDF", "DOCX" } },
            { ".xlsx", new[] { "PDF" } },
            { ".xls",  new[] { "PDF" } },

            // Images
            { ".jpg",  new[] { "PNG", "BMP", "WEBP", "ICO", "PDF" } },
            { ".jpeg", new[] { "PNG", "BMP", "WEBP", "ICO", "PDF" } },
            { ".png",  new[] { "JPG", "BMP", "WEBP", "ICO", "PDF" } },
            { ".bmp",  new[] { "JPG", "PNG", "WEBP", "ICO", "PDF" } },
            { ".webp", new[] { "JPG", "PNG", "PDF" } },
            { ".ico",  new[] { "PNG", "JPG" } },

            // Audio
            { ".mp3",  new[] { "WAV", "AAC", "FLAC", "M4A", "OGG" } },
            { ".wav",  new[] { "MP3", "AAC", "FLAC", "M4A", "OGG" } },
            { ".flac", new[] { "MP3", "WAV", "AAC", "M4A", "OGG" } },
            { ".m4a",  new[] { "MP3", "WAV", "AAC", "FLAC", "OGG" } },
            { ".aac",  new[] { "MP3", "WAV", "FLAC", "M4A", "OGG" } },
            { ".ogg",  new[] { "MP3", "WAV", "AAC", "FLAC", "M4A" } },

            // Video
            { ".mp4",  new[] { "MP3", "AVI", "MOV", "GIF", "WEBM", "MKV" } },
            { ".mov",  new[] { "MP4", "AVI", "GIF", "MP3" } },
            { ".avi",  new[] { "MP4", "MOV", "GIF", "MP3" } },
            { ".mkv",  new[] { "MP4", "AVI", "MOV", "MP3" } },
            { ".webm", new[] { "MP4", "AVI", "MOV", "MP3" } },
        };

    /// <summary>Get the media category for a given file extension.</summary>
    public static string GetCategory(string extension)
    {
        if (VideoExtensions.Contains(extension)) return "Video";
        if (AudioExtensions.Contains(extension)) return "Audio";
        if (ImageExtensions.Contains(extension)) return "Image";
        if (DocumentExtensions.Contains(extension)) return "Document";
        return "Unknown";
    }
}
