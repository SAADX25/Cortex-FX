using System.Security.Cryptography;
using CortexFX.Core.Audio;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;
using NAudio.Wave;
using Xunit;

namespace CortexFX.Tests;

public sealed class AudioSeparationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "CortexFX-AudioTests-" + Guid.NewGuid().ToString("N"));
    public AudioSeparationTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData(1)] [InlineData(44100)] [InlineData(1323000)] [InlineData(1323001)] [InlineData(6000000)]
    public void ChunksCoverEverySampleOnceWithBoundedContext(long frames)
    {
        var chunks = AudioStemSeparationService.PlanChunks(frames);
        long next = 0;
        foreach (var c in chunks)
        {
            Assert.Equal(next, c.StartFrame);
            Assert.InRange(c.FrameCount, 1, 30L * 44100);
            Assert.InRange(c.ReadStartFrame, 0, c.StartFrame);
            Assert.True(c.ReadStartFrame + c.ReadFrameCount <= frames);
            Assert.True(c.ReadStartFrame + c.ReadFrameCount >= c.StartFrame + c.FrameCount);
            Assert.True(c.ReadFrameCount <= 34L * 44100);
            next += c.FrameCount;
        }
        Assert.Equal(frames, next);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioStemSeparationService.PlanChunks(0));
    }

    [Fact]
    public void ContextIsDiscardedWithoutLosingOrDuplicatingSamples()
    {
        const int frames = 32 * 44100 + 17;
        byte[] samples = new byte[frames * 4]; new Random(17).NextBytes(samples);
        string output = Path.Combine(_folder, "joined.wav");
        using (var writer = new WaveFileWriter(output, new WaveFormat(44100, 16, 2)))
        {
            foreach (var chunk in AudioStemSeparationService.PlanChunks(frames))
            {
                string part = Path.Combine(_folder, "part.wav");
                using (var partWriter = new WaveFileWriter(part, writer.WaveFormat))
                    partWriter.Write(samples, (int)(chunk.ReadStartFrame * 4), (int)(chunk.ReadFrameCount * 4));
                AudioStemSeparationService.AppendCore(part, writer, chunk, default);
            }
        }
        using var reader = new WaveFileReader(output);
        byte[] joined = new byte[reader.Length]; reader.ReadExactly(joined);
        Assert.Equal(samples, joined);
    }

    [Fact]
    public async Task CancelledAndFailedJobsDoNotPublishPartialStems()
    {
        using var processes = new TestProcesses();
        string ffmpeg = Path.Combine(_folder, "ffmpeg.exe");
        foreach (string name in new[] { "ffmpeg.exe", "sherpa-onnx-offline-source-separation.exe", AudioStemSeparationService.ModelFileName, "onnxruntime.dll", "onnxruntime_providers_shared.dll" })
            File.WriteAllText(Path.Combine(_folder, name), "test fixture");
        string source = Path.Combine(_folder, "original.wav"); File.WriteAllText(source, "unchanged");
        string output = Path.Combine(_folder, "results");
        var service = new AudioStemSeparationService(processes, ffmpeg, _folder);
        var request = new AudioSeparationRequest(source, output, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SeparateAsync(request,
            new InlineProgress(p => { if (p.Percent >= 5) cts.Cancel(); }), cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(output));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SeparateAsync(request));
        Assert.Empty(Directory.GetFileSystemEntries(output));
        Assert.Equal("unchanged", File.ReadAllText(source));
    }

    [Fact]
    public async Task MissingEngineAndInvalidRangesFailBeforePublishing()
    {
        using var processes = new FakeProcesses();
        var service = new AudioStemSeparationService(processes, "missing.exe", _folder);
        Assert.False(service.IsAvailable);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SeparateAsync(new("missing", _folder, TimeSpan.Zero, TimeSpan.Zero)));
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [LocalSeparationFact]
    public async Task RealModelExportsTwoDifferentStemsForTheRequestedRangeAndPreservesTheSource()
    {
        string source = Path.Combine(_folder, "تجربة الصوت mixed track.wav");
        File.Copy(LocalSeparationFactAttribute.Sample, source);
        byte[] before = SHA256.HashData(File.ReadAllBytes(source));
        using var processes = new ProcessManager();
        var service = new AudioStemSeparationService(processes, BundledFFmpegFactAttribute.Executable, LocalSeparationFactAttribute.Engine);
        var request = new AudioSeparationRequest(source, _folder, TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(3.5)) { OutputFormat = AudioSeparationFormat.Wav };
        var progress = new List<double>();
        var first = await service.SeparateAsync(request, new InlineProgress(p => progress.Add(p.Percent)));
        var second = await service.SeparateAsync(request);
        Assert.NotEqual(first.Folder, second.Folder);
        using var vocals = new WaveFileReader(first.VocalsPath);
        using var music = new WaveFileReader(first.MusicPath);
        Assert.Equal(44100, vocals.WaveFormat.SampleRate);
        Assert.Equal(2, vocals.WaveFormat.Channels);
        Assert.InRange(vocals.TotalTime.TotalSeconds, 3.499, 3.501);
        Assert.Equal(vocals.Length, music.Length);
        byte[] voice = new byte[vocals.Length], instrumental = new byte[music.Length];
        vocals.ReadExactly(voice); music.ReadExactly(instrumental);
        Assert.Contains(voice, b => b != 0); Assert.Contains(instrumental, b => b != 0);
        Assert.False(voice.SequenceEqual(instrumental));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Empty(Directory.GetDirectories(_folder, ".cortex-stems-*"));
        Assert.Equal(100, progress[^1]);
        Assert.True(progress.Zip(progress.Skip(1)).All(p => p.First <= p.Second));
    }

    [LocalSeparationFact]
    public async Task RealModelReassemblesMultipleChunksWithoutChangingTheSampleCount()
    {
        string source = Path.Combine(_folder, "long mix.wav");
        using (var sample = new WaveFileReader(LocalSeparationFactAttribute.Sample))
        using (var writer = new WaveFileWriter(source, sample.WaveFormat))
        {
            byte[] buffer = new byte[65536];
            long left = 32L * 44100 * 4 + 17 * 4;
            while (left > 0)
            {
                int read = sample.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0) { sample.Position = 0; continue; }
                writer.Write(buffer, 0, read); left -= read;
            }
        }
        using var processes = new ProcessManager();
        var service = new AudioStemSeparationService(processes, BundledFFmpegFactAttribute.Executable, LocalSeparationFactAttribute.Engine);
        var result = await service.SeparateAsync(new(source, _folder, TimeSpan.Zero, TimeSpan.FromSeconds(33)) { OutputFormat = AudioSeparationFormat.Wav });
        using var input = new WaveFileReader(source);
        using var vocals = new WaveFileReader(result.VocalsPath);
        using var music = new WaveFileReader(result.MusicPath);
        Assert.Equal(input.Length, vocals.Length);
        Assert.Equal(input.Length, music.Length);
    }
    [Theory]
    [InlineData("Available providers: CPUExecutionProvider. Fallback to cpu!")]
    [InlineData("Falling back to CPU because CUDA could not initialize")]
    public void RequestedGpuNeverSilentlyUsesTheCpu(string log) =>
        Assert.Throws<AudioGpuUnavailableException>(() => AudioStemSeparationService.EnsureGpuWasUsed(log));

    [Fact]
    public void SizeEstimatesExplainTheMp3SpaceSaving()
    {
        var duration = TimeSpan.FromSeconds(223.21);
        long mp3 = AudioStemSeparationService.EstimateStemBytes(duration, AudioSeparationFormat.Mp3);
        long wav = AudioStemSeparationService.EstimateStemBytes(duration, AudioSeparationFormat.Wav);
        Assert.InRange(mp3, 5357000, 5358000);
        Assert.InRange(wav, 39370000, 39380000);
        Assert.True(mp3 < wav / 7);
        Assert.Equal(AudioSeparationFormat.Mp3, new AudioSeparationRequest("a", "b", TimeSpan.Zero, duration).OutputFormat);
        AudioStemSeparationService.EnsureGpuWasUsed("CUDAExecutionProvider · some shape nodes assigned to CPU");
    }

    [LocalSeparationFact]
    public async Task DefaultMp3OutputIsCompressedAndContainsNoWavFiles()
    {
        using var processes = new ProcessManager();
        var service = new AudioStemSeparationService(processes, BundledFFmpegFactAttribute.Executable, LocalSeparationFactAttribute.Engine);
        var result = await service.SeparateAsync(new(LocalSeparationFactAttribute.Sample, _folder, TimeSpan.Zero, TimeSpan.FromSeconds(4)));
        Assert.Equal(AudioSeparationFormat.Mp3, result.OutputFormat);
        Assert.Equal(AudioSeparationProcessor.Cpu, result.ProcessorUsed);
        foreach (var path in new[] { result.VocalsPath, result.MusicPath })
        {
            Assert.Equal(".mp3", Path.GetExtension(path));
            Assert.InRange(new FileInfo(path).Length, 90000, 110000);
            using var mp3 = new Mp3FileReader(path);
            Assert.InRange(mp3.TotalTime.TotalSeconds, 3.9, 4.15);
            Assert.Equal(2, mp3.WaveFormat.Channels);
            Assert.True(new FileInfo(path).Length < 4 * 44100 * 4 / 5);
        }
        Assert.Empty(Directory.GetFiles(result.Folder, "*.wav"));
        Assert.Equal(2, Directory.GetFiles(result.Folder).Length);
    }

    [Fact]
    public async Task EncodingFailureDoesNotPublishOneFinishedStem()
    {
        string source = Path.Combine(_folder, "source.wav"); File.WriteAllText(source, "original");
        foreach (string name in new[] { "ffmpeg.exe", "sherpa-onnx-offline-source-separation.exe", AudioStemSeparationService.ModelFileName, "onnxruntime.dll", "onnxruntime_providers_shared.dll" })
            File.WriteAllText(Path.Combine(_folder, name), "fixture");
        using var processes = new EncodingFailureProcesses();
        var service = new AudioStemSeparationService(processes, Path.Combine(_folder, "ffmpeg.exe"), _folder);
        string output = Path.Combine(_folder, "results");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SeparateAsync(new(source, output, TimeSpan.Zero, TimeSpan.FromSeconds(1))));
        Assert.Empty(Directory.GetFileSystemEntries(output));
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public async Task GpuRequestWithoutCompatibleHardwareDoesNotStartACpuJob()
    {
        string source = Path.Combine(_folder, "source.wav"); File.WriteAllText(source, "original");
        foreach (string name in new[] { "ffmpeg.exe", "sherpa-onnx-offline-source-separation.exe", AudioStemSeparationService.ModelFileName, "onnxruntime.dll", "onnxruntime_providers_shared.dll" })
            File.WriteAllText(Path.Combine(_folder, name), "fixture");
        Directory.CreateDirectory(Path.Combine(_folder, "Gpu"));
        foreach (string name in AudioStemSeparationService.RequiredGpuFiles) File.WriteAllText(Path.Combine(_folder, "Gpu", name), "fixture");
        using var processes = new EncodingFailureProcesses();
        var service = new AudioStemSeparationService(processes, Path.Combine(_folder, "ffmpeg.exe"), _folder, () => new(false, "", "No compatible NVIDIA GPU"));
        string output = Path.Combine(_folder, "results");
        await Assert.ThrowsAsync<AudioGpuUnavailableException>(() => service.SeparateAsync(new(source, output, TimeSpan.Zero, TimeSpan.FromSeconds(1)) { Processor = AudioSeparationProcessor.Gpu }));
        Assert.False(Directory.Exists(output));
    }

    [LocalGpuSeparationFact]
    public async Task RealCudaSeparationExportsMp3AndReportsTheGpuProcessor()
    {
        using var processes = new ProcessManager();
        var service = new AudioStemSeparationService(processes, BundledFFmpegFactAttribute.Executable, LocalSeparationFactAttribute.Engine);
        var progress = new List<string>();
        var result = await service.SeparateAsync(new(LocalSeparationFactAttribute.Sample, _folder, TimeSpan.Zero, TimeSpan.FromSeconds(4))
            { Processor = AudioSeparationProcessor.Gpu }, new InlineProgress(p => progress.Add(p.Message)));
        Assert.Equal(AudioSeparationProcessor.Gpu, result.ProcessorUsed);
        Assert.Equal(AudioSeparationFormat.Mp3, result.OutputFormat);
        Assert.Contains(progress, m => m.Contains("NVIDIA GPU"));
        using var mp3 = new Mp3FileReader(result.VocalsPath);
        Assert.InRange(mp3.TotalTime.TotalSeconds, 3.9, 4.15);
        Assert.InRange(new FileInfo(result.MusicPath).Length, 90000, 110000);
    }

    private sealed class EncodingFailureProcesses : FakeProcesses
    {
        public override Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            string LastPath() => System.Text.RegularExpressions.Regex.Matches(args, "\"([^\"]+)\"").Last().Groups[1].Value;
            void Wav(string path) { using var w = new WaveFileWriter(path, new WaveFormat(44100,16,2)); w.Write(new byte[44100*4],0,44100*4); }
            if (args.Contains("--output-vocals-wav"))
            {
                string Value(string option) => System.Text.RegularExpressions.Regex.Match(args, option + "=\"([^\"]+)\"").Groups[1].Value;
                Wav(Value("--output-vocals-wav")); Wav(Value("--output-accompaniment-wav"));
            }
            else if (args.Contains("libmp3lame"))
            {
                if (LastPath().EndsWith("Instrumental.mp3")) throw new InvalidOperationException("Simulated encoding failure");
                File.WriteAllText(LastPath(), "ID3 fixture");
            }
            else Wav(LastPath());
            return Task.FromResult(new ProcessResult(0,"",""));
        }
    }
    private sealed class InlineProgress(Action<AudioSeparationProgress> report) : IProgress<AudioSeparationProgress>
    { public void Report(AudioSeparationProgress value) => report(value); }
    private sealed class TestProcesses : FakeProcesses, IProcessManager
    {
        public override Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!exe.EndsWith("ffmpeg.exe")) throw new InvalidOperationException("Simulated inference failure");
            string path = args[(args.LastIndexOf('"', args.Length - 2) + 1)..^1];
            using var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 2));
            writer.Write(new byte[44100 * 4], 0, 44100 * 4);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}

public sealed class LocalSeparationFactAttribute : FactAttribute
{
    public static string Engine => Environment.GetEnvironmentVariable("CORTEXFX_TEST_SEPARATION_DIR") ?? Path.Combine(AppContext.BaseDirectory, "Resources", "AudioSeparation");
    public static string Sample => Environment.GetEnvironmentVariable("CORTEXFX_TEST_AUDIO_SAMPLE") ?? "";
    public LocalSeparationFactAttribute()
    {
        if (!File.Exists(Path.Combine(Engine, AudioStemSeparationService.ModelFileName)) || !File.Exists(Sample) || !File.Exists(BundledFFmpegFactAttribute.Executable))
            Skip = "Requires the bundled local AI engine and CORTEXFX_TEST_AUDIO_SAMPLE.";
    }
}
public sealed class LocalGpuSeparationFactAttribute : FactAttribute
{
    public LocalGpuSeparationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEXFX_TEST_GPU") != "1" || !AudioGpuDeviceDetector.Detect().Available ||
            !File.Exists(Path.Combine(LocalSeparationFactAttribute.Engine, "Gpu", "onnxruntime_providers_cuda.dll")))
            Skip = "Requires CORTEXFX_TEST_GPU=1 and the NVIDIA GPU engine.";
    }
}