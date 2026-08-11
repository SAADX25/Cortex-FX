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
        ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls", ".odt", ".rtf", ".txt"
    };

    public static readonly IReadOnlySet<string> PdfExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public static readonly IReadOnlySet<string> WordExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".odt", ".rtf", ".txt"
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
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".gif", ".heic", ".heif"
    };

    public static readonly IReadOnlySet<string> RasterImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff", ".gif", ".heic", ".heif"
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

    // --- Archives ---
    public static readonly IReadOnlySet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar"
    };

    // --- E-books ---
    public static readonly IReadOnlySet<string> EbookExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".mobi", ".azw3"
    };

    // --- FFmpeg-handled output formats ---
    public static readonly IReadOnlySet<string> FFmpegOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mp3", "avi", "wav", "mkv", "mov", "gif", "webm", "m4a", "ogg", "flac", "aac"
    };

    /// <summary>FFmpeg audio-only extraction formats (use -vn flag).</summary>
    public static readonly IReadOnlySet<string> AudioOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "aac", "flac", "m4a", "ogg"
    };

    /// <summary>Video container formats produced through FFmpeg.</summary>
    public static readonly IReadOnlySet<string> VideoOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "avi", "mkv", "mov", "webm"
    };

    // --- Magick-handled output formats ---
    public static readonly IReadOnlySet<string> MagickOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "bmp", "webp", "ico", "tif", "tiff", "gif", "heic", "heif"
    };

    public static readonly IReadOnlySet<string> LibreOfficeOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "docx", "pptx", "xlsx", "odt", "rtf", "txt"
    };

    public static readonly IReadOnlySet<string> ArchiveOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "7z", "tar"
    };

    public static readonly IReadOnlySet<string> EbookOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "epub", "mobi", "azw3", "pdf"
    };

    // --- All supported extensions (union for global validation) ---
    public static readonly IReadOnlySet<string> AllSupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls", ".odt", ".rtf", ".txt",
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".gif", ".heic", ".heif",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg",
        ".mp4", ".avi", ".mov", ".mkv", ".webm",
        ".zip", ".rar", ".7z", ".tar",
        ".epub", ".mobi", ".azw3"
    };

    // --- Conversion Rules (input extension → available target formats) ---
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ConversionRules =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            // Documents
            { ".pdf",  new[] { "DOCX", "PPTX", "JPG", "PNG", "WEBP", "TIFF", "EPUB", "MOBI", "AZW3" } },
            { ".docx", new[] { "PDF", "PPTX", "ODT", "RTF", "TXT" } },
            { ".doc",  new[] { "PDF", "PPTX", "DOCX", "ODT", "RTF", "TXT" } },
            { ".pptx", new[] { "PDF", "DOCX" } },
            { ".ppt",  new[] { "PDF", "PPTX", "DOCX" } },
            { ".xlsx", new[] { "PDF" } },
            { ".xls",  new[] { "PDF", "XLSX" } },
            { ".odt",  new[] { "PDF", "DOCX", "RTF", "TXT" } },
            { ".rtf",  new[] { "PDF", "DOCX", "ODT", "TXT" } },
            { ".txt",  new[] { "PDF", "DOCX", "ODT", "RTF" } },

            // Images
            { ".jpg",  new[] { "PNG", "BMP", "WEBP", "ICO", "TIFF", "GIF", "HEIC", "PDF" } },
            { ".jpeg", new[] { "PNG", "BMP", "WEBP", "ICO", "TIFF", "GIF", "HEIC", "PDF" } },
            { ".png",  new[] { "JPG", "BMP", "WEBP", "ICO", "TIFF", "GIF", "HEIC", "PDF" } },
            { ".bmp",  new[] { "JPG", "PNG", "WEBP", "ICO", "TIFF", "GIF", "HEIC", "PDF" } },
            { ".webp", new[] { "JPG", "PNG", "BMP", "TIFF", "GIF", "PDF" } },
            { ".ico",  new[] { "PNG", "JPG", "WEBP" } },
            { ".tif",  new[] { "JPG", "PNG", "WEBP", "BMP", "PDF" } },
            { ".tiff", new[] { "JPG", "PNG", "WEBP", "BMP", "PDF" } },
            { ".gif",  new[] { "JPG", "PNG", "WEBP", "BMP", "PDF" } },
            { ".heic", new[] { "JPG", "PNG", "WEBP", "PDF" } },
            { ".heif", new[] { "JPG", "PNG", "WEBP", "PDF" } },

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

            // Archives (requires 7-Zip when converting between archive formats)
            { ".zip", new[] { "7Z", "TAR" } },
            { ".rar", new[] { "ZIP", "7Z", "TAR" } },
            { ".7z",  new[] { "ZIP", "TAR" } },
            { ".tar", new[] { "ZIP", "7Z" } },

            // E-books (requires Calibre ebook-convert)
            { ".epub", new[] { "MOBI", "AZW3", "PDF" } },
            { ".mobi", new[] { "EPUB", "AZW3", "PDF" } },
            { ".azw3", new[] { "EPUB", "MOBI", "PDF" } },
        };

    /// <summary>Get the media category for a given file extension.</summary>
    public static string GetCategory(string extension)
    {
        if (VideoExtensions.Contains(extension)) return "Video";
        if (AudioExtensions.Contains(extension)) return "Audio";
        if (ImageExtensions.Contains(extension)) return "Image";
        if (DocumentExtensions.Contains(extension)) return "Document";
        if (ArchiveExtensions.Contains(extension)) return "Archive";
        if (EbookExtensions.Contains(extension)) return "Ebook";
        return "Unknown";
    }
}
