using System.IO.Compression;
using CortexFX.Core.Configuration;
using CortexFX.Core.Constants;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Documents;
using CortexFX.Core.Services.Infrastructure;
using CortexFX.Core.Services.Media;
using CortexFX.Models;
using Xunit;

namespace CortexFX.Tests;

public class ConversionTests
{
    [Theory]
    [InlineData("picture.png", "jpg", "magick")]
    [InlineData("picture.png", "pdf", "magick")]
    [InlineData("movie.mp4", "mp3", "ffmpeg-audio")]
    [InlineData("movie.mp4", "mkv", "ffmpeg-video")]
    [InlineData("song.wav", "mp3", "ffmpeg-audio")]
    [InlineData("paper.pdf", "docx", "pdf-word")]
    public async Task RouterSelectsEngine(string file, string target, string expected)
    {
        var fake = new FakeEngines();
        var result = await fake.Router.ConvertAsync(Job(file, target));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, fake.Called);
    }

    [Fact]
    public async Task UnsupportedConversionFails()
    {
        var result = await new FakeEngines().Router.ConvertAsync(Job("unknown.xyz", "abc"));
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CancellationStopsRouting()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeEngines();
        var result = await fake.Router.ConvertAsync(Job("song.wav", "mp3"), cts.Token);
        Assert.False(result.Success);
        Assert.Null(fake.Called);
    }

    [Fact]
    public async Task SamePathGetsDifferentOutputName()
    {
        var fake = new FakeEngines();
        var result = await fake.Router.ConvertAsync(Job("same.png", "png"));
        Assert.Contains("same_converted.png", result.OutputPath);
    }

    [Fact]
    public async Task HardwareFailureRetriesInSoftware()
    {
        var fake = new FakeEngines { FailHardware = true };
        var result = await fake.Router.ConvertAsync(Job("movie.mp4", "mkv"));
        Assert.True(result.Success);
        Assert.Equal(2, fake.VideoAttempts);
        Assert.Equal("software", fake.LastArguments);
    }

    [Fact]
    public async Task BatchCancellationReturnsOneResultPerJob()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var jobs = Enumerable.Range(0, 5).Select(i => Job($"image{i}.png", "jpg")).ToArray();
        var results = await new FakeEngines().Router.ConvertBatchAsync(jobs, cts.Token);
        Assert.Equal(jobs.Length, results.Length);
        Assert.All(results, result => Assert.False(result.Success));
    }

    [Fact]
    public async Task ImageBatchUsesThreeEngineSlots()
    {
        var fake = new FakeEngines { ImageDelay = TimeSpan.FromMilliseconds(30) };
        var jobs = Enumerable.Range(0, 9).Select(i => Job($"image{i}.png", "jpg")).ToArray();
        var router = fake.Router;
        router.MaxParallelism = 8;
        var results = await router.ConvertBatchAsync(jobs);
        Assert.All(results, result => Assert.True(result.Success));
        Assert.InRange(fake.MaxConcurrentImages, 1, 3);
    }

    [Theory]
    [InlineData(".mp4", "Video")]
    [InlineData(".mp3", "Audio")]
    [InlineData(".png", "Image")]
    [InlineData(".pdf", "Document")]
    [InlineData(".zip", "Archive")]
    public void CategoriesAreStable(string ext, string category) => Assert.Equal(category, MediaTypes.GetCategory(ext));

    [Fact]
    public void ConversionRulesIncludePdfToWord() => Assert.Contains("DOCX", MediaTypes.ConversionRules[".pdf"]);

    [Theory]
    [InlineData("mp4", "libx264")]
    [InlineData("mkv", "libx264")]
    [InlineData("webm", "libvpx-vp9")]
    [InlineData("avi", "mpeg4")]
    public void VideoArgumentsSelectExpectedCodec(string format, string codec)
    {
        var service = new FFmpegService(new FakeConfig("."), new FakeProcesses());
        Assert.Contains(codec, service.BuildVideoArguments("in.mp4", $"out.{format}", 75, format, false));
    }

    [Fact]
    public void AudioArgumentsAndQualityChange()
    {
        var service = new FFmpegService(new FakeConfig("."), new FakeProcesses());
        Assert.Contains("libmp3lame", service.BuildAudioArguments("in.wav", "out.mp3", "mp3"));
        Assert.Contains("pcm_s16le", service.BuildAudioArguments("in.mp3", "out.wav", "wav"));
        Assert.NotEqual(service.BuildAudioArguments("in.wav", "out.mp3", "mp3", 10),
                        service.BuildAudioArguments("in.wav", "out.mp3", "mp3", 90));
        Assert.True(service.UsesHardwareEncoder("-c:v h264_nvenc"));
        Assert.False(service.UsesHardwareEncoder("-c:v libx264"));
    }

    [Fact]
    public void ResourceValidationReportsMissingAndCompleteSetups()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CortexFX_Test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ResourceValidationService(new FakeConfig(dir));
            Assert.False(service.ValidateCoreResources().ResourcesDirectoryExists);
            Directory.CreateDirectory(dir);
            var missing = service.ValidateCoreResources();
            Assert.Contains("ffmpeg.exe", missing.MissingTools);
            Assert.Contains("magick.exe", missing.MissingTools);
            Assert.Contains("pdftocairo.exe", missing.MissingTools);
            Assert.Contains("avcodec-58.dll", missing.MissingFFmpegDlls);
            foreach (string name in missing.MissingTools) File.WriteAllText(Path.Combine(dir, name), "x");
            Directory.CreateDirectory(Path.Combine(dir, "ffmpeg_libs"));
            foreach (string name in missing.MissingFFmpegDlls) File.WriteAllText(Path.Combine(dir, "ffmpeg_libs", name), "x");
            Assert.True(service.ValidateCoreResources().IsReady);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static ConversionJob Job(string file, string target) => new()
    {
        InputPath = Path.Combine(Path.GetTempPath(), file), OutputDirectory = Path.GetTempPath(),
        TargetFormat = target, CreateSubfolder = false
    };
}

internal sealed class FakeEngines : IFFmpegService, IMagickService, IOfficeInteropService,
    IPdfRenderService, IOptionalConversionService, IPdfToWordService
{
    public string? Called { get; private set; }
    public bool WordInstalled { get; set; } = true;
    public bool PowerPointInstalled { get; set; } = true;
    public bool ExcelInstalled { get; set; } = true;
    public bool OptionalSupported { get; set; }
    public bool OptionalInstalled { get; set; }
    public bool FailHardware { get; set; }
    public TimeSpan ImageDelay { get; set; }
    private int _concurrentImages;
    public int MaxConcurrentImages { get; private set; }
    public int VideoAttempts { get; private set; }
    public string? LastArguments { get; private set; }
    public ConversionRouter Router => new(new FakeConfig("."), this, this, this, this, this, this);
    public string BuildVideoArguments(string a, string b, double q, string f, bool preferHardware = true) => preferHardware ? "hardware" : "software";
    public string BuildAudioArguments(string a, string b, string? f = null, double q = 75) => "audio";
    public bool UsesHardwareEncoder(string args) => args == "hardware";
    public Task ConvertAsync(string input, string output, string args, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        LastArguments = args;
        Called = args == "audio" ? "ffmpeg-audio" : "ffmpeg-video";
        if (args != "audio") VideoAttempts++;
        if (FailHardware && args == "hardware") throw new ProcessExecutionException("ffmpeg.exe", 1, "", "encoder failed");
        return Task.CompletedTask;
    }
    public Task ConvertAsync(string input, string output, CancellationToken ct = default, IProgress<double>? progress = null)
    { ct.ThrowIfCancellationRequested(); Called = "pdf-word"; return Task.CompletedTask; }
    public async Task ConvertImageAsync(string a, string b, ImageConversionOptions o, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Called = "magick";
        int active = Interlocked.Increment(ref _concurrentImages);
        MaxConcurrentImages = Math.Max(MaxConcurrentImages, active);
        try { if (ImageDelay > TimeSpan.Zero) await Task.Delay(ImageDelay, ct); }
        finally { Interlocked.Decrement(ref _concurrentImages); }
    }
    public ImageMetadata? ReadMetadata(string path) => null;
    public Task MergeImagesToPdfAsync(IReadOnlyList<string> a, string b, CancellationToken ct = default) => Task.CompletedTask;
    public Task TrimAudioAsync(string a, string b, TimeSpan s, TimeSpan e, CancellationToken ct = default) => Task.CompletedTask;
    public Task CutVideoSegmentsAsync(string a, string b, IReadOnlyList<VideoCutSegment> s, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public Task ExtractAudioAsync(string a, string b, CancellationToken ct = default, IProgress<double>? p = null)
    { ct.ThrowIfCancellationRequested(); Called = "ffmpeg-audio"; return Task.CompletedTask; }
    public Task ConvertToGifAsync(string a, string b, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public bool IsOfficeInstalled() => WordInstalled;
    public bool IsApplicationInstalled(string application) => application switch
    { "Word" => WordInstalled, "Excel" => ExcelInstalled, "PowerPoint" => PowerPointInstalled, _ => false };
    public Task ConvertWordDocumentAsync(string a, string b, string target, CancellationToken ct = default, IProgress<double>? p = null)
    { ct.ThrowIfCancellationRequested(); Called = "word-" + target; return Task.CompletedTask; }
    public Task ConvertToPdfAsync(string a, string b, int q = 1, CancellationToken ct = default, IProgress<double>? p = null)
    { ct.ThrowIfCancellationRequested(); Called = "office-pdf"; return Task.CompletedTask; }
    public Task ConvertPdfToWordAsync(string a, string b, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public Task ConvertWordToPowerPointAsync(string a, string b, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public Task ConvertPdfToPowerPointAsync(string a, string b, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public Task RenderPdfToImagesAsync(string a, string b, string f, int dpi = 150, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public bool CanConvert(string a, string b) => OptionalSupported;
    public string? GetUnavailableReason(string a, string b) => OptionalInstalled ? null : "Optional engine is missing.";
    public Task<ConversionResult> ConvertAsync(string a, string b, string ext, string target, CancellationToken ct = default, IProgress<double>? p = null)
    { ct.ThrowIfCancellationRequested(); Called = "optional"; return Task.FromResult(ConversionResult.Ok(b)); }
}

internal sealed class FakeConfig(string dir) : IAppConfiguration
{
    public string ResourcesDirectory => dir;
    public string FFmpegPath => Path.Combine(dir, "ffmpeg.exe");
    public string MagickPath => Path.Combine(dir, "magick.exe");
    public string PdfToCairoPath => Path.Combine(dir, "pdftocairo.exe");
    public string FFmpegLibsDirectory => Path.Combine(dir, "ffmpeg_libs");
}

internal class FakeProcesses : IProcessManager
{
    public ProcessResult RunSync(string e, string a, CancellationToken ct = default) => new(0, "", "");
    public virtual Task<ProcessResult> RunAsync(string e, string a, CancellationToken ct = default) => Task.FromResult(RunSync(e, a, ct));
    public void TrackProcess(int pid) { }
    public bool IsTrackedProcess(int pid) => false;
    public void UntrackProcess(int pid) { }
    public void KillTrackedProcess(int pid) { }
    public void KillAllTracked() { }
    public void KillZombieProcesses(params string[] names) { }
    public void Dispose() { }
}
