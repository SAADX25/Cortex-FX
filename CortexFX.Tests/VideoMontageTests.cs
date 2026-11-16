using System.Diagnostics;
using CortexFX.Core.Configuration;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Services.Media;
using CortexFX.Core.Video;
using Xunit;

namespace CortexFX.Tests;

public sealed class VideoMontageTests
{
    [Fact]
    public void MontageProjectSplitRemoveReorderAndMapWorkAccurately()
    {
        var project = new MontageProject();
        var assetA = project.RegisterAsset(new VideoMediaInfo(
            @"C:\media\clipA.mp4", TimeSpan.FromSeconds(20),
            1080, 1920, 1080, 1920, 0, 30, "h264", "aac", true, true, "mp4", 1024));
        var assetB = project.RegisterAsset(new VideoMediaInfo(
            @"C:\media\clipB.mov", TimeSpan.FromSeconds(10),
            1920, 1080, 1920, 1080, 0, 60, "h264", "", true, false, "mov", 2048));

        MontageClipItem c1 = project.AddClipFromAsset(assetA);
        Assert.Equal(TimeSpan.FromSeconds(20), project.TotalDuration);
        Assert.Equal("9:16", c1.AspectRatioLabel);

        // Split clipA at 6s -> [0..6], [6..20]
        var (splitOk, c2) = project.SplitAtMontageTime(TimeSpan.FromSeconds(6));
        Assert.True(splitOk);
        Assert.NotNull(c2);
        Assert.Equal(2, project.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(6), project.Clips[0].End);
        Assert.Equal(TimeSpan.FromSeconds(6), project.Clips[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(6), project.Clips[1].TimelineStart);
        Assert.Equal(TimeSpan.FromSeconds(20), project.TotalDuration);

        // Split c2 again at 12s -> [0..6] (6s), [6..12] (6s), [12..20] (8s) and remove middle slice
        var (split2Ok, c2Tail) = project.SplitAtMontageTime(TimeSpan.FromSeconds(12));
        Assert.True(split2Ok);
        Assert.NotNull(c2Tail);
        Assert.True(project.RemoveClip(c2!));
        Assert.Equal(2, project.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(14), project.TotalDuration);

        // Add clipB (10s silent 1080p) and move earlier before c2Tail
        MontageClipItem c3 = project.AddClipFromAsset(assetB);
        Assert.Equal(TimeSpan.FromSeconds(24), project.TotalDuration);
        Assert.True(project.MoveClipEarlier(c3));
        Assert.Equal(new[] { c1, c3, c2Tail! }, project.Clips.ToArray());
        Assert.Equal(1, c1.OrderIndex);
        Assert.Equal(2, c3.OrderIndex);
        Assert.Equal(3, c2Tail!.OrderIndex);
        Assert.Equal(TimeSpan.FromSeconds(16), c2Tail.TimelineStart);

        // Map montage time 18s -> c2Tail at source time 14s
        var (mappedClip, idx, srcTime) = project.MapMontageToSource(TimeSpan.FromSeconds(18));
        Assert.Same(c2Tail, mappedClip);
        Assert.Equal(2, idx);
        Assert.Equal(TimeSpan.FromSeconds(14), srcTime);

        // Duplicate & Remove
        MontageClipItem? dup = project.DuplicateClip(c3);
        Assert.NotNull(dup);
        Assert.Equal(4, project.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(34), project.TotalDuration);
        Assert.True(project.RemoveClip(dup!));
        Assert.Equal(3, project.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(24), project.TotalDuration);
    }

    [Fact]
    public void ParseVideoProbeOutputHandlesRotationSarSilentAndWebm()
    {
        const string rotatedPhoneOutput = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'phone.mp4':
              Duration: 00:00:52.54, start: 0.000000, bitrate: 14200 kb/s
              Stream #0:0(und): Video: h264 (High), yuv420p(tv, bt709), 1920x1080 [SAR 1:1 DAR 16:9], 30 fps, 30 tbr
                Metadata:
                  rotate          : 90
                Side data:
                  displaymatrix: rotation of -90.00 degrees
              Stream #0:1(und): Audio: aac (LC), 48000 Hz, stereo, fltp
            """;

        VideoMediaInfo phone = FFmpegService.ParseVideoProbeOutput(rotatedPhoneOutput, "phone.mp4", 5000);
        Assert.True(phone.HasVideo);
        Assert.True(phone.HasAudio);
        Assert.Equal(1920, phone.CodedWidth);
        Assert.Equal(1080, phone.CodedHeight);
        Assert.Equal(1080, phone.DisplayWidth);
        Assert.Equal(1920, phone.DisplayHeight);
        Assert.Equal("9:16", phone.AspectRatioLabel);
        Assert.Equal(30, phone.Fps);
        Assert.Equal("h264", phone.VideoCodec);
        Assert.Equal("aac", phone.AudioCodec);

        const string silentAnamorphicWebm = """
            Input #0, matroska,webm, from 'clip.webm':
              Duration: 00:01:05.20, start: 0.000000, bitrate: 900 kb/s
              Stream #0:0: Video: vp9 (Profile 0), yuv420p(tv), 720x576, SAR 16:15 DAR 4:3, 60 fps
            """;

        VideoMediaInfo webm = FFmpegService.ParseVideoProbeOutput(silentAnamorphicWebm, "clip.webm", 1234);
        Assert.True(webm.HasVideo);
        Assert.False(webm.HasAudio);
        Assert.Equal(768, webm.DisplayWidth);
        Assert.Equal(576, webm.DisplayHeight);
        Assert.Equal("4:3", webm.AspectRatioLabel);
        Assert.Equal(60, webm.Fps);
    }

    [Fact]
    public async Task RealFfmpegExportsMixedResolutionsAndFormatsInExactOrder()
    {
        string ffmpegPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Resources", "ffmpeg.exe"));
        if (!File.Exists(ffmpegPath))
            return; // Skip only if binary not present in environment.

        string tempDir = Path.Combine(Path.GetTempPath(), "CortexFX_MontageInt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var pm = new ProcessManager();
            var service = new FFmpegService(new TestFfmpegConfig(ffmpegPath), pm);

            // Create 3 heterogeneous inputs:
            // 1) Vertical 720x1280 MP4 with audio (first 2s blue, next 2s red)
            string vertMp4 = Path.Combine(tempDir, "vert.mp4");
            await RunRawFfmpegAsync(ffmpegPath,
                $"-hide_banner -loglevel error -f lavfi -i \"color=c=blue:size=720x1280:rate=30:duration=2\" " +
                $"-f lavfi -i \"color=c=red:size=720x1280:rate=30:duration=2\" " +
                $"-f lavfi -i \"sine=frequency=440:duration=4\" " +
                $"-filter_complex \"[0:v][1:v]concat=n=2:v=1:a=0[v]\" -map \"[v]\" -map 2:a -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -y \"{vertMp4}\"");

            // 2) Horizontal 1280x720 silent MKV (no audio stream!)
            string silentMkv = Path.Combine(tempDir, "silent.mkv");
            await RunRawFfmpegAsync(ffmpegPath,
                $"-hide_banner -loglevel error -f lavfi -i \"smptebars=size=1280x720:rate=24:duration=2.5\" " +
                $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -y \"{silentMkv}\"");

            // 3) Odd-resolution 853x481 WebM VP9/Opus (first scale to even inside source gen, or raw odd libx264/vp9)
            string squareWebm = Path.Combine(tempDir, "square.webm");
            await RunRawFfmpegAsync(ffmpegPath,
                $"-hide_banner -loglevel error -f lavfi -i \"color=c=green:size=640x640:rate=60:duration=2.5\" " +
                $"-f lavfi -i \"sine=frequency=660:duration=2.5\" " +
                $"-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -c:a libopus -y \"{squareWebm}\"");

            // Verify ProbeVideoAsync
            VideoMediaInfo p1 = await service.ProbeVideoAsync(vertMp4);
            VideoMediaInfo p2 = await service.ProbeVideoAsync(silentMkv);
            VideoMediaInfo p3 = await service.ProbeVideoAsync(squareWebm);
            Assert.Equal(720, p1.DisplayWidth);
            Assert.Equal(1280, p1.DisplayHeight);
            Assert.True(p1.HasAudio);
            Assert.False(p2.HasAudio);
            Assert.Equal(640, p3.DisplayWidth);

            // Export 1: Mixed montage (Portrait + Silent Landscape + 60fps Square WebM) -> 1080p MP4 FitPad
            string outMp4 = Path.Combine(tempDir, "mixed_1080.mp4");
            var clips = new List<VideoMontageClip>
            {
                new(vertMp4, TimeSpan.FromSeconds(2.2), TimeSpan.FromSeconds(3.7)), // Red half first!
                new(silentMkv, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.0)),
                new(squareWebm, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.7))
            };

            await service.ExportMontageAsync(new VideoMontageExportRequest(
                Clips: clips,
                OutputPath: outMp4,
                OutputFormat: "mp4",
                CutMode: VideoCutMode.AccurateEncode,
                Resolution: VideoResolutionPreset.Fhd1080,
                AspectFitMode: VideoAspectFitMode.FitPad));

            Assert.True(File.Exists(outMp4));
            VideoMediaInfo outInfo = await service.ProbeVideoAsync(outMp4);
            Assert.Equal(1080, outInfo.DisplayWidth);
            Assert.Equal(1920, outInfo.DisplayHeight);
            Assert.True(outInfo.HasAudio);
            Assert.InRange(outInfo.Duration.TotalSeconds, 4.2, 4.8);

            // Export 2: Square 1:1 FillCrop MOV
            string outMov = Path.Combine(tempDir, "square_fill.mov");
            await service.ExportMontageAsync(new VideoMontageExportRequest(
                Clips: clips.Take(2).ToList(),
                OutputPath: outMov,
                OutputFormat: "mov",
                CutMode: VideoCutMode.AccurateEncode,
                Resolution: VideoResolutionPreset.Square1080x1080,
                AspectFitMode: VideoAspectFitMode.FillCrop));

            Assert.True(File.Exists(outMov));
            VideoMediaInfo movInfo = await service.ProbeVideoAsync(outMov);
            Assert.Equal(1080, movInfo.DisplayWidth);
            Assert.Equal(1080, movInfo.DisplayHeight);

            // Export 3: Verify reversed segment order in CutVideoSegmentsAsync (red [2.3..3.5] BEFORE blue [0.2..1.4])
            string reversedCut = Path.Combine(tempDir, "reversed_order.mp4");
            await service.CutVideoSegmentsAsync(vertMp4, reversedCut, new[]
            {
                new VideoCutSegment(TimeSpan.FromSeconds(2.3), TimeSpan.FromSeconds(3.5)),
                new VideoCutSegment(TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.4))
            });
            Assert.True(File.Exists(reversedCut));
            VideoMediaInfo revInfo = await service.ProbeVideoAsync(reversedCut);
            Assert.InRange(revInfo.Duration.TotalSeconds, 2.2, 2.6);

            // Pixel verification: first clip (at 0.4s) must be RED, second clip (at 1.8s) must be BLUE.
            (byte r1, byte g1, byte b1) = await ReadCenterRgbAsync(ffmpegPath, reversedCut, 0.4);
            (byte r2, byte g2, byte b2) = await ReadCenterRgbAsync(ffmpegPath, reversedCut, 1.8);
            Assert.True(r1 > 180 && b1 < 80, $"Expected RED first segment, got RGB({r1},{g1},{b1})");
            Assert.True(b2 > 180 && r2 < 80, $"Expected BLUE second segment, got RGB({r2},{g2},{b2})");

            // Export 4: WEBM + AVI format exports
            string outWebm = Path.Combine(tempDir, "montage.webm");
            await service.ExportMontageAsync(new VideoMontageExportRequest(
                Clips: new[] { new VideoMontageClip(vertMp4, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5)) },
                OutputPath: outWebm,
                OutputFormat: "webm",
                CutMode: VideoCutMode.AccurateEncode,
                Resolution: VideoResolutionPreset.Sd480));
            VideoMediaInfo webmOut = await service.ProbeVideoAsync(outWebm);
            Assert.Equal(480, webmOut.DisplayWidth);
            Assert.Equal(854, webmOut.DisplayHeight);

            string outAvi = Path.Combine(tempDir, "montage.avi");
            await service.ExportMontageAsync(new VideoMontageExportRequest(
                Clips: new[] { new VideoMontageClip(silentMkv, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.2)) },
                OutputPath: outAvi,
                OutputFormat: "avi",
                CutMode: VideoCutMode.AccurateEncode,
                Resolution: VideoResolutionPreset.Hd720));
            Assert.True(File.Exists(outAvi));
            Assert.True(new FileInfo(outAvi).Length > 1000);

            // Export 5: FastLossless copy
            string losslessOut = Path.Combine(tempDir, "lossless.mp4");
            await service.ExportMontageAsync(new VideoMontageExportRequest(
                Clips: new[] { new VideoMontageClip(vertMp4, TimeSpan.FromSeconds(0.0), TimeSpan.FromSeconds(2.0)) },
                OutputPath: losslessOut,
                OutputFormat: "mp4",
                CutMode: VideoCutMode.FastLossless,
                Resolution: VideoResolutionPreset.Original));
            Assert.True(File.Exists(losslessOut));
            Assert.True(new FileInfo(losslessOut).Length > 1000);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MontageProject_UndoRedo_RestoresSplitRemoveDuplicateAndCheckpoint()
    {
        var project = new MontageProject();
        var asset = project.RegisterAsset(new VideoMediaInfo(
            @"C:\fake\sample.mp4", TimeSpan.FromSeconds(10),
            1920, 1080, 1920, 1080, 0, 30, "h264", "aac", true, true, "mp4", 1_000_000));

        var clip = project.AddClipFromAsset(asset);
        project.ClearHistory();
        Assert.False(project.CanUndo);
        Assert.False(project.CanRedo);

        var (splitOk, second) = project.SplitAtMontageTime(TimeSpan.FromSeconds(4));
        Assert.True(splitOk);
        Assert.NotNull(second);
        Assert.Equal(2, project.Clips.Count);
        Assert.True(project.CanUndo);

        // Simulate batch video append checkpoint via BeginUndoStep / CommitUndoStep
        project.BeginUndoStep();
        project.AddClipFromAsset(asset);
        project.CommitUndoStep();
        Assert.Equal(3, project.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(20), project.TotalDuration);

        // Duplicate second clip -> 4 clips
        Assert.NotNull(project.DuplicateClip(second!));
        Assert.Equal(4, project.Clips.Count);

        // Undo Duplicate -> back to 3 clips (20s)
        Assert.True(project.Undo());
        Assert.Equal(3, project.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(20), project.TotalDuration);

        // Undo Batch Append -> back to 2 clips (10s)
        Assert.True(project.Undo());
        Assert.Equal(2, project.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), project.TotalDuration);

        // Undo Split -> back to 1 clip (10s)
        Assert.True(project.Undo());
        Assert.Single(project.Clips);
        Assert.False(project.CanUndo);
        Assert.True(project.CanRedo);

        // Redo back forward
        Assert.True(project.Redo());
        Assert.Equal(2, project.Clips.Count);
        Assert.True(project.Redo());
        Assert.Equal(TimeSpan.FromSeconds(20), project.TotalDuration);
    }

    [Fact]
    public void SourceMediaAsset_ProgressiveWavesAndBoundedMatching_WorkCleanly()
    {
        var (wave1, wave2, spacing) = SourceMediaAsset.BuildProgressiveSampleWaves(170.0);
        Assert.Equal(14, wave1.Length);
        Assert.Equal(14, wave2.Length);
        Assert.InRange(spacing, 12.0, 12.3);

        for (int i = 0; i < wave1.Length; i++)
        {
            Assert.True(wave1[i] < wave2[i], $"Wave1[{i}] ({wave1[i]}) should precede Wave2[{i}] ({wave2[i]}).");
            if (i + 1 < wave1.Length)
                Assert.True(wave2[i] < wave1[i + 1], $"Wave2[{i}] ({wave2[i]}) should precede Wave1[{i + 1}] ({wave1[i + 1]}).");
        }
        Assert.True(wave2[^1] <= 169.8);

        var asset = new SourceMediaAsset(
            new VideoMediaInfo(@"C:\fake\movie.mp4", TimeSpan.FromSeconds(170), 1920, 1080, 1920, 1080, 0, 24, "av1", "aac", true, true, "mp4", 1024),
            "#38BDF8");

        var imgA = CreateTinyBitmap();
        var imgB = CreateTinyBitmap();
        var imgBReplacement = CreateTinyBitmap();

        asset.AddOrUpdateFrame(new FilmstripFrame(30.0, imgB));
        asset.AddOrUpdateFrame(new FilmstripFrame(5.0, imgA));
        Assert.Equal(2, asset.Frames.Count);
        Assert.Equal(5.0, asset.Frames[0].PositionSeconds);
        Assert.Equal(30.0, asset.Frames[1].PositionSeconds);

        asset.AddOrUpdateFrame(new FilmstripFrame(30.02, imgBReplacement));
        Assert.Equal(2, asset.Frames.Count);
        Assert.Same(imgBReplacement, asset.Frames[1].Image);

        // Bounded snap avoids smearing early frames onto far unloaded tiles while extracting.
        double maxSnap = spacing * 0.68;
        Assert.Same(imgA, asset.FindNearestFrame(6.0, maxSnap));
        Assert.Null(asset.FindNearestFrame(80.0, maxSnap));
        // Unbounded fallback still fills any slot after extraction finishes.
        Assert.Same(imgBReplacement, asset.FindNearestFrame(80.0));
        Assert.True(asset.HasFrameNear(5.1, 0.25));
        Assert.False(asset.HasFrameNear(80.0, 0.25));
    }

    private static System.Windows.Media.Imaging.BitmapSource CreateTinyBitmap()
    {
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
        bmp.Freeze();
        return bmp;
    }

    private static async Task<(byte R, byte G, byte B)> ReadCenterRgbAsync(string ffmpegPath, string videoPath, double seconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-hide_banner -loglevel error -ss {seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} -i \"{videoPath}\" -frames:v 1 -vf \"scale=1:1\" -f rawvideo -pix_fmt rgb24 pipe:1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        using var ms = new MemoryStream();
        Task<string> err = proc.StandardError.ReadToEndAsync();
        await proc.StandardOutput.BaseStream.CopyToAsync(ms);
        await proc.WaitForExitAsync();
        _ = await err;
        byte[] rgb = ms.ToArray();
        Assert.True(rgb.Length >= 3, "Failed to sample RGB pixel.");
        return (rgb[0], rgb[1], rgb[2]);
    }

    private static async Task RunRawFfmpegAsync(string ffmpegPath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi)!;
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.True(proc.ExitCode == 0, $"FFmpeg setup failed: {stderr}");
    }

    private sealed class TestFfmpegConfig(string ffmpegPath) : IAppConfiguration
    {
        public string ResourcesDirectory => Path.GetDirectoryName(ffmpegPath)!;
        public string FFmpegPath => ffmpegPath;
        public string MagickPath => string.Empty;
        public string PdfToCairoPath => string.Empty;
        public string FFmpegLibsDirectory => Path.Combine(ResourcesDirectory, "ffmpeg_libs");
    }
}
