using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CortexFX.Core.Video;

public sealed class SavedAssetRecord
{
    public string FilePath { get; set; } = string.Empty;
    public long DurationTicks { get; set; }
    public int CodedWidth { get; set; }
    public int CodedHeight { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }
    public int RotationDegrees { get; set; }
    public double Fps { get; set; } = 30.0;
    public string VideoCodec { get; set; } = string.Empty;
    public string AudioCodec { get; set; } = string.Empty;
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public string ContainerFormat { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string ColorHex { get; set; } = "#38BDF8";
    public int BinOrderIndex { get; set; }
}

public sealed class SavedClipRecord
{
    public Guid Id { get; set; }
    public Guid? LinkedClipId { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public long StartTicks { get; set; }
    public long EndTicks { get; set; }
    public long TimelineStartTicks { get; set; }
    public TimelineTrackKind TrackKind { get; set; }
    public int TrackLayer { get; set; }
    public double AudioVolume { get; set; } = 1.0;
    public double VideoScale { get; set; } = 1.0;
    public double VideoOffsetX { get; set; }
    public double VideoOffsetY { get; set; }
    public double VideoOpacity { get; set; } = 1.0;
}

public sealed class MontageProjectDocument
{
    public int Version { get; set; } = 1;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = "Untitled Montage";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsAutoSave { get; set; }
    public string? LinkedProjectId { get; set; }
    public string? CoverThumbnailBase64 { get; set; }

    public List<SavedAssetRecord> Assets { get; set; } = new();
    public List<SavedClipRecord> Clips { get; set; } = new();
    public Guid? SelectedClipId { get; set; }

    public long PlayheadPositionTicks { get; set; }
    public bool[] VideoTrackMuted { get; set; } = new bool[MontageProject.MaxVideoLayers];
    public bool[] AudioTrackMuted { get; set; } = new bool[MontageProject.MaxAudioTracks];
    public bool IsMuted { get; set; }
    public double Volume { get; set; } = 1.0;
    public double SpeedRatio { get; set; } = 1.0;
    public bool IsSnapEnabled { get; set; } = true;
    public bool IsLoopEnabled { get; set; }
    public bool IsAudioExpanded { get; set; }
    public bool IsTransformMode { get; set; }
    public double ZoomSliderValue { get; set; }
    public double TimelineHorizontalOffset { get; set; }

    public string RenderMode { get; set; } = "SmartRender";
    public string OutputFormat { get; set; } = "mp4";
    public string ResolutionPreset { get; set; } = "Original";
    public string AspectFitMode { get; set; } = "FitPad";
    public string? OutputFolderOverride { get; set; }
}

public sealed class SavedProjectSummary
{
    private ImageSource? _coverThumbnail;
    private bool _coverThumbnailDecoded;

    public string ProjectId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = "Untitled Montage";
    public string FilePath { get; init; } = string.Empty;
    public DateTime UpdatedUtc { get; init; }
    public int MediaCount { get; init; }
    public int ClipCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public bool IsAutoSave { get; init; }
    public bool IsExternalFile { get; init; }
    public string? LinkedProjectId { get; init; }
    public string? CoverThumbnailBase64 { get; init; }

    public ImageSource? CoverThumbnail
    {
        get
        {
            if (!_coverThumbnailDecoded)
            {
                _coverThumbnailDecoded = true;
                _coverThumbnail = MontageProjectStorage.DecodeThumbnail(CoverThumbnailBase64);
            }
            return _coverThumbnail;
        }
    }

    public string DisplayDuration => MontageProject.FormatShort(TotalDuration);

    public string DisplayMeta => IsAutoSave
        ? $"Auto-saved draft · {ClipCount} clip{(ClipCount == 1 ? "" : "s")} · {DisplayDuration}"
        : $"{MediaCount} media · {ClipCount} clip{(ClipCount == 1 ? "" : "s")} · {DisplayDuration}";

    public string DisplayTime => FormatRelativeTimestamp(UpdatedUtc);

    private static string FormatRelativeTimestamp(DateTime utc)
    {
        DateTime local = utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;
        TimeSpan age = DateTime.Now - local;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (local.Date == DateTime.Today) return $"Today {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday {local:HH:mm}";
        return local.ToString("MMM d, HH:mm");
    }
}

/// <summary>
/// Persists Video Cutter / Montage Studio editing progress as atomic JSON .cfxproj files
/// in %LocalAppData%\CortexFX\VideoProjects or user-chosen paths.
/// </summary>
public static class MontageProjectStorage
{
    public const string FileExtension = ".cfxproj";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ProjectsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CortexFX",
        "VideoProjects");

    public static string AutoSaveFilePath => Path.Combine(ProjectsDirectory, "_autosave" + FileExtension);
    private static string RecentIndexFilePath => Path.Combine(ProjectsDirectory, "recent_projects.json");

    public static string GetManagedProjectPath(string projectId)
    {
        string safeId = SanitizeFileName(projectId);
        if (string.IsNullOrEmpty(safeId))
            safeId = Guid.NewGuid().ToString("N");
        return Path.Combine(ProjectsDirectory, safeId + FileExtension);
    }

    public static string BuildDefaultProjectName(IEnumerable<SourceMediaAsset> assets)
    {
        SourceMediaAsset? first = assets.FirstOrDefault();
        if (first == null)
            return $"Montage {DateTime.Now:MMM d HH-mm}";

        string stem = Path.GetFileNameWithoutExtension(first.FileName);
        string clean = SanitizeDisplayName(stem);
        return string.IsNullOrEmpty(clean) ? $"Montage {DateTime.Now:MMM d HH-mm}" : clean;
    }

    public static string SanitizeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Untitled Montage";
        string trimmed = name.Trim();
        return trimmed.Length > 64 ? trimmed[..64].Trim() : trimmed;
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        char[] invalid = Path.GetInvalidFileNameChars();
        var filtered = name.Trim().Where(ch => Array.IndexOf(invalid, ch) < 0).ToArray();
        return new string(filtered);
    }

    public static async Task SaveDocumentAsync(string filePath, MontageProjectDocument doc)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(doc, JsonOptions);
        string tempPath = filePath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
        File.Move(tempPath, filePath, overwrite: true);
    }

    public static void SaveDocumentSync(string filePath, MontageProjectDocument doc)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(doc, JsonOptions);
        string tempPath = filePath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }

    public static async Task<MontageProjectDocument?> LoadDocumentAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MontageProjectDocument>(json, JsonOptions);
    }

    public static MontageProjectDocument? LoadDocumentSync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<MontageProjectDocument>(json, JsonOptions);
    }

    public static void DeleteAutoSave()
    {
        try
        {
            if (File.Exists(AutoSaveFilePath))
                File.Delete(AutoSaveFilePath);
        }
        catch { }
    }

    public static void RegisterRecentExternalProject(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            string full = Path.GetFullPath(filePath);
            if (IsInsideManagedDirectory(full)) return;

            List<string> paths = LoadRecentExternalPaths();
            paths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            paths.Insert(0, full);
            if (paths.Count > 12)
                paths.RemoveRange(12, paths.Count - 12);
            SaveRecentExternalPaths(paths);
        }
        catch { }
    }

    public static void DeleteSavedProject(SavedProjectSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.FilePath)) return;

        try
        {
            if (summary.IsAutoSave || !summary.IsExternalFile)
            {
                if (File.Exists(summary.FilePath))
                    File.Delete(summary.FilePath);
            }
            else
            {
                List<string> paths = LoadRecentExternalPaths();
                if (paths.RemoveAll(p => string.Equals(p, summary.FilePath, StringComparison.OrdinalIgnoreCase)) > 0)
                    SaveRecentExternalPaths(paths);

                // Also clean up any managed mirror of this external project so it doesn't reappear as a duplicate
                if (!string.IsNullOrWhiteSpace(summary.ProjectId))
                {
                    string mirrorPath = GetManagedProjectPath(summary.ProjectId);
                    if (File.Exists(mirrorPath))
                        File.Delete(mirrorPath);
                }
            }
        }
        catch { }
    }

    public static IReadOnlyList<SavedProjectSummary> ListSavedProjects()
    {
        var results = new List<SavedProjectSummary>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddFile(string path, bool isExternal)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!seenPaths.Add(full) || !File.Exists(full))
                    return;

                MontageProjectDocument? doc = LoadDocumentSync(full);
                if (doc == null || (doc.Assets.Count == 0 && doc.Clips.Count == 0))
                    return;

                TimeSpan totalDuration = ComputeSummaryDuration(doc);
                int logicalClips = CountLogicalClips(doc);

                results.Add(new SavedProjectSummary
                {
                    ProjectId = doc.ProjectId,
                    ProjectName = SanitizeDisplayName(doc.ProjectName),
                    FilePath = full,
                    UpdatedUtc = doc.UpdatedUtc,
                    MediaCount = doc.Assets.Count,
                    ClipCount = logicalClips,
                    TotalDuration = totalDuration,
                    IsAutoSave = doc.IsAutoSave || string.Equals(full, Path.GetFullPath(AutoSaveFilePath), StringComparison.OrdinalIgnoreCase),
                    IsExternalFile = isExternal,
                    LinkedProjectId = doc.LinkedProjectId,
                    CoverThumbnailBase64 = doc.CoverThumbnailBase64
                });
            }
            catch { }
        }

        if (File.Exists(AutoSaveFilePath))
            TryAddFile(AutoSaveFilePath, isExternal: false);

        if (Directory.Exists(ProjectsDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(ProjectsDirectory, "*" + FileExtension))
            {
                if (string.Equals(Path.GetFileName(file), "_autosave" + FileExtension, StringComparison.OrdinalIgnoreCase))
                    continue;
                TryAddFile(file, isExternal: false);
            }
        }

        List<string> recentExternal = LoadRecentExternalPaths();
        bool pruned = false;
        var validRecent = new List<string>(recentExternal.Count);
        foreach (string extPath in recentExternal)
        {
            if (File.Exists(extPath))
            {
                validRecent.Add(extPath);
                TryAddFile(extPath, isExternal: true);
            }
            else
            {
                pruned = true;
            }
        }
        if (pruned)
            SaveRecentExternalPaths(validRecent);

        // Deduplicate non-autosave entries sharing the same ProjectId (e.g. managed mirror + external .cfxproj)
        var deduped = new List<SavedProjectSummary>(results.Count);
        var byProjectId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (SavedProjectSummary item in results)
        {
            if (item.IsAutoSave || string.IsNullOrWhiteSpace(item.ProjectId))
            {
                deduped.Add(item);
                continue;
            }

            if (byProjectId.TryGetValue(item.ProjectId, out int existingIdx))
            {
                SavedProjectSummary existing = deduped[existingIdx];
                bool preferNew = item.UpdatedUtc > existing.UpdatedUtc ||
                                 (item.UpdatedUtc == existing.UpdatedUtc && item.IsExternalFile && !existing.IsExternalFile);
                if (preferNew)
                    deduped[existingIdx] = item;
            }
            else
            {
                byProjectId[item.ProjectId] = deduped.Count;
                deduped.Add(item);
            }
        }

        return deduped
            .OrderByDescending(s => s.IsAutoSave)
            .ThenByDescending(s => s.UpdatedUtc)
            .ToList();
    }

    private static TimeSpan ComputeSummaryDuration(MontageProjectDocument doc)
    {
        long maxEnd = 0;
        foreach (SavedClipRecord c in doc.Clips)
        {
            long dur = Math.Max(0, c.EndTicks - c.StartTicks);
            long end = Math.Max(0, c.TimelineStartTicks) + dur;
            if (end > maxEnd) maxEnd = end;
        }
        return TimeSpan.FromTicks(maxEnd);
    }

    private static int CountLogicalClips(MontageProjectDocument doc)
    {
        var videoIds = new HashSet<Guid>();
        int videoCount = 0;
        foreach (SavedClipRecord c in doc.Clips)
        {
            if (c.TrackKind == TimelineTrackKind.Video)
            {
                videoIds.Add(c.Id);
                videoCount++;
            }
        }

        int standaloneAudio = 0;
        foreach (SavedClipRecord c in doc.Clips)
        {
            if (c.TrackKind == TimelineTrackKind.Audio &&
                (!c.LinkedClipId.HasValue || !videoIds.Contains(c.LinkedClipId.Value)))
            {
                standaloneAudio++;
            }
        }

        return videoCount + standaloneAudio;
    }

    private static bool IsInsideManagedDirectory(string fullPath)
    {
        string managedDir = Path.GetFullPath(ProjectsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(managedDir, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> LoadRecentExternalPaths()
    {
        try
        {
            if (File.Exists(RecentIndexFilePath))
            {
                string json = File.ReadAllText(RecentIndexFilePath);
                return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
            }
        }
        catch { }
        return new List<string>();
    }

    private static void SaveRecentExternalPaths(List<string> paths)
    {
        try
        {
            Directory.CreateDirectory(ProjectsDirectory);
            string json = JsonSerializer.Serialize(paths, JsonOptions);
            File.WriteAllText(RecentIndexFilePath, json);
        }
        catch { }
    }

    public static string? EncodeThumbnailBase64(ImageSource? image)
    {
        if (image is not BitmapSource bmp)
            return null;

        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 72 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static ImageSource? DecodeThumbnail(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
