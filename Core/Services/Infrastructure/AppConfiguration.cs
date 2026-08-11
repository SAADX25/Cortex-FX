using System.IO;
using CortexFX.Core.Configuration;

namespace CortexFX.Core.Services.Infrastructure;

/// <summary>
/// Resolves tool paths under Resources/. In Debug, falls back to the project folder if needed.
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
    /// Prefer {app}\Resources; in Debug also try the project folder.
    /// </summary>
    private static string ResolveResourcesDirectory()
    {
        string baseDirResources = Path.Combine(AppContext.BaseDirectory, "Resources");
        if (Directory.Exists(baseDirResources))
        {
            return baseDirResources;
        }

#if DEBUG
        // Running from bin\Debug — walk up until we find the .csproj folder
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
    /// Find ffmpeg_libs (looks for avcodec-58.dll under the app folder if needed).
    /// </summary>
    private string ResolveFFmpegLibsDirectory()
    {
        string standardPath = Path.Combine(ResourcesDirectory, "ffmpeg_libs");
        string markerDll = Path.Combine(standardPath, "avcodec-58.dll");

        if (File.Exists(markerDll))
        {
            return standardPath;
        }

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
            // No access — keep the default path
        }

        return standardPath;
    }

    /// <summary>Walk up until a .csproj is found (dev builds only).</summary>
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
