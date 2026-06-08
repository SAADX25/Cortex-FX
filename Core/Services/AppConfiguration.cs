using System.IO;
using CortexFX.Core.Configuration;

namespace CortexFX.Core.Services;

/// <summary>
/// Centralized configuration implementation.
/// Resolves all tool paths relative to the application base directory,
/// with a dev-time fallback for the IDE scenario.
/// </summary>
public sealed class AppConfiguration : IAppConfiguration
{
    public string ResourcesDirectory { get; }
    public string FFmpegPath { get; }
    public string MagickPath { get; }
    public string PdfToCairoPath { get; }
    public string FFmpegLibsDirectory { get; }

    public AppConfiguration()
    {
        ResourcesDirectory = ResolveResourcesDirectory();
        FFmpegPath = Path.Combine(ResourcesDirectory, "ffmpeg.exe");
        MagickPath = Path.Combine(ResourcesDirectory, "magick.exe");
        PdfToCairoPath = Path.Combine(ResourcesDirectory, "pdftocairo.exe");
        FFmpegLibsDirectory = ResolveFFmpegLibsDirectory();
    }

    /// <summary>
    /// Resolves the Resources directory.
    /// Priority: 1) {BaseDirectory}/Resources  2) Project-relative fallback (dev only)
    /// </summary>
    private static string ResolveResourcesDirectory()
    {
        string baseDirResources = Path.Combine(AppContext.BaseDirectory, "Resources");
        if (Directory.Exists(baseDirResources))
        {
            return baseDirResources;
        }

#if DEBUG
        // Dev-time fallback: walk up from bin/Debug/net10.0-windows to project root
        string? projectRoot = FindProjectRoot(AppContext.BaseDirectory);
        if (projectRoot != null)
        {
            string devResources = Path.Combine(projectRoot, "Resources");
            if (Directory.Exists(devResources))
            {
                return devResources;
            }
        }
#endif

        // Return the standard path even if it doesn't exist yet —
        // MainWindow_Loaded will warn the user.
        return baseDirResources;
    }

    /// <summary>
    /// Resolves the FFmpeg shared libraries directory.
    /// Uses auto-discovery if the standard path doesn't contain the expected DLL.
    /// </summary>
    private string ResolveFFmpegLibsDirectory()
    {
        string standardPath = Path.Combine(ResourcesDirectory, "ffmpeg_libs");
        string markerDll = Path.Combine(standardPath, "avcodec-58.dll");

        if (File.Exists(markerDll))
        {
            return standardPath;
        }

        // Auto-discovery: search recursively from base directory
        string baseDir = AppContext.BaseDirectory;
        try
        {
            string? foundFile = Directory.GetFiles(baseDir, "avcodec-58.dll", SearchOption.AllDirectories)
                                         .FirstOrDefault();
            if (foundFile != null)
            {
                return Path.GetDirectoryName(foundFile)!;
            }
        }
        catch
        {
            // Access denied or other IO errors — fall through
        }

        return standardPath;
    }

    /// <summary>
    /// Walks up from a directory looking for a .csproj file to identify the project root.
    /// Used only for dev-time path resolution.
    /// </summary>
    private static string? FindProjectRoot(string startDir)
    {
        DirectoryInfo? dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            try
            {
                if (dir.GetFiles("*.csproj").Length > 0)
                {
                    return dir.FullName;
                }
            }
            catch
            {
                return null;
            }

            dir = dir.Parent;
        }
        return null;
    }
}
