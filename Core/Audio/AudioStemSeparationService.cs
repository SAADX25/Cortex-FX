using System.Globalization;
using System.IO;
using CortexFX.Core.Interfaces;
using NAudio.Wave;

namespace CortexFX.Core.Audio;

public enum AudioSeparationProcessor { Cpu, Gpu }
public enum AudioSeparationFormat { Mp3, Wav }
public sealed record AudioSeparationRequest(string SourcePath, string OutputParent, TimeSpan Start, TimeSpan Duration)
{
    public AudioSeparationProcessor Processor { get; init; } = AudioSeparationProcessor.Cpu;
    public AudioSeparationFormat OutputFormat { get; init; } = AudioSeparationFormat.Mp3;
}
public sealed record AudioSeparationProgress(double Percent, string Message);
public sealed record AudioSeparationResult(string Folder, string VocalsPath, string MusicPath)
{
    public AudioSeparationProcessor ProcessorUsed { get; init; }
    public AudioSeparationFormat OutputFormat { get; init; }
}
internal sealed record AudioSeparationChunk(long StartFrame, long FrameCount, long ReadStartFrame, long ReadFrameCount);

/// <summary>Local UVR inference in bounded chunks. Publishes both stems together only after success.</summary>
public sealed class AudioStemSeparationService(IProcessManager processes, string ffmpegPath, string engineDirectory, Func<AudioGpuDevice>? gpuDetector = null)
{
    public const string ModelFileName = "UVR-MDX-NET-Voc_FT.onnx";
    internal const int SampleRate = 44100;
    internal const int BlockAlign = 4; // Stereo PCM16, same format as the native engine output.
    private string EnginePath => Path.Combine(engineDirectory, "sherpa-onnx-offline-source-separation.exe");
    private string ModelPath => Path.Combine(engineDirectory, ModelFileName);
    private string GpuEnginePath => Path.Combine(engineDirectory, "Gpu", "sherpa-onnx-offline-source-separation.exe");
    internal static readonly string[] RequiredGpuFiles = ["sherpa-onnx-offline-source-separation.exe", "onnxruntime.dll",
        "onnxruntime_providers_cuda.dll", "onnxruntime_providers_shared.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll", "cudnn64_9.dll", "msvcp140.dll", "vcruntime140.dll"];
    public bool IsGpuEngineAvailable => RequiredGpuFiles.All(name => File.Exists(Path.Combine(engineDirectory, "Gpu", name)));
    public const int Mp3BitrateKbps = 192;
    public static long EstimateStemBytes(TimeSpan duration, AudioSeparationFormat format) => format switch
    {
        AudioSeparationFormat.Mp3 => (long)Math.Ceiling(Math.Max(0, duration.TotalSeconds) * Mp3BitrateKbps * 1000 / 8),
        AudioSeparationFormat.Wav => (long)Math.Ceiling(Math.Max(0, duration.TotalSeconds) * SampleRate * BlockAlign) + 44,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    public bool IsAvailable => new[] { ffmpegPath, EnginePath, ModelPath,
        Path.Combine(engineDirectory, "onnxruntime.dll"), Path.Combine(engineDirectory, "onnxruntime_providers_shared.dll") }.All(File.Exists);

    public Task<AudioSeparationResult> SeparateAsync(AudioSeparationRequest request,
        IProgress<AudioSeparationProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => SeparateCoreAsync(request, progress, ct), ct);

    private async Task<AudioSeparationResult> SeparateCoreAsync(AudioSeparationRequest request,
        IProgress<AudioSeparationProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Start < TimeSpan.Zero || request.Duration <= TimeSpan.Zero)
            throw new ArgumentException("Select a non-empty audio range.");
        if (!File.Exists(request.SourcePath)) throw new FileNotFoundException("The source audio file is missing.", request.SourcePath);
        if (!IsAvailable) throw new FileNotFoundException("The local AI engine is incomplete. Reinstall the Audio Studio build with its Resources folder.");
        if (!Enum.IsDefined(request.Processor) || !Enum.IsDefined(request.OutputFormat))
            throw new ArgumentException("Choose a valid processor and output format.");
        if (request.Processor == AudioSeparationProcessor.Gpu)
        {
            if (!IsGpuEngineAvailable) throw new AudioGpuUnavailableException("GPU engine files are missing. Use the GPU build or choose CPU.");
            var device = (gpuDetector ?? AudioGpuDeviceDetector.Detect)();
            if (!device.Available) throw new AudioGpuUnavailableException(device.Reason);
        }
        ct.ThrowIfCancellationRequested();
        string parent = Path.GetFullPath(request.OutputParent);
        Directory.CreateDirectory(parent);
        string work = Path.Combine(Path.GetTempPath(), "CortexFX-Audio-" + Guid.NewGuid().ToString("N"));
        string pending = Path.Combine(parent, ".cortex-stems-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(pending);
            progress?.Report(new(0, "Preparing audio on your device…"));
            string decoded = Path.Combine(work, "input.wav");
            string start = request.Start.TotalSeconds.ToString("F9", CultureInfo.InvariantCulture);
            string duration = request.Duration.TotalSeconds.ToString("F9", CultureInfo.InvariantCulture);
            await processes.RunAsync(ffmpegPath,
                $"-hide_banner -loglevel error -nostdin -y -ss {start} -i {Quote(request.SourcePath)} -t {duration} -map 0:a:0 -vn -ar {SampleRate} -ac 2 -c:a pcm_s16le {Quote(decoded)}", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string vocalsWav = Path.Combine(work, "Vocals-full.wav");
            string musicWav = Path.Combine(work, "Instrumental-full.wav");
            using (var source = new WaveFileReader(decoded))
            {
                ValidateFormat(source);
                long frames = source.Length / BlockAlign;
                if (frames == 0) throw new InvalidDataException("The selected range contains no audio.");
                var chunks = PlanChunks(frames);
                using var vocals = new WaveFileWriter(vocalsWav, source.WaveFormat);
                using var music = new WaveFileWriter(musicWav, source.WaveFormat);
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunks[i];
                    string inputChunk = Path.Combine(work, "chunk.wav");
                    string vocalChunk = Path.Combine(work, "vocals.wav");
                    string musicChunk = Path.Combine(work, "music.wav");
                    WriteChunk(source, inputChunk, chunk, ct);
                    bool gpu = request.Processor == AudioSeparationProcessor.Gpu;
                    progress?.Report(new(5 + 85.0 * i / chunks.Count, $"Separating part {i + 1} of {chunks.Count} · {(gpu ? "NVIDIA GPU" : "CPU")}"));
                    ProcessResult inference;
                    try
                    {
                        inference = await processes.RunAsync(gpu ? GpuEnginePath : EnginePath,
                            $"--uvr-model={Quote(ModelPath)} --input-wav={Quote(inputChunk)} --output-vocals-wav={Quote(vocalChunk)} --output-accompaniment-wav={Quote(musicChunk)} --num-threads={Math.Clamp(Environment.ProcessorCount / 2, 1, 4)} --provider={(gpu ? "cuda" : "cpu")} --print-args=false", ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (gpu && ex is not OperationCanceledException)
                    {
                        throw new AudioGpuUnavailableException("GPU processing failed. Close other GPU apps, check the NVIDIA driver, or choose CPU.", ex);
                    }
                    if (gpu) EnsureGpuWasUsed(inference.StdOut + "\n" + inference.StdErr);
                    ct.ThrowIfCancellationRequested();
                    AppendCore(vocalChunk, vocals, chunk, ct);
                    AppendCore(musicChunk, music, chunk, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            string extension = request.OutputFormat == AudioSeparationFormat.Mp3 ? ".mp3" : ".wav";
            string vocalsOutput = Path.Combine(pending, "Vocals" + extension);
            string musicOutput = Path.Combine(pending, "Instrumental" + extension);
            if (request.OutputFormat == AudioSeparationFormat.Mp3)
            {
                progress?.Report(new(92, "Encoding vocals · MP3 192 kbps…"));
                await EncodeMp3Async(vocalsWav, vocalsOutput, ct).ConfigureAwait(false);
                progress?.Report(new(96, "Encoding instrumental · MP3 192 kbps…"));
                await EncodeMp3Async(musicWav, musicOutput, ct).ConfigureAwait(false);
            }
            else
            {
                File.Move(vocalsWav, vocalsOutput);
                File.Move(musicWav, musicOutput);
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(99, "Saving both stems…"));
            string sourceName = Path.GetFileNameWithoutExtension(request.SourcePath);
            if (sourceName.Length > 70) sourceName = sourceName[..70];
            // Directory.Move is atomic on this volume; no partial stems or overwritten previous exports.
            string folder = PublishFolder(pending, parent, sourceName + "_Stems", ct);
            progress?.Report(new(100, "Vocals and instrumental are ready."));
            return new(folder, Path.Combine(folder, "Vocals" + extension), Path.Combine(folder, "Instrumental" + extension))
            { ProcessorUsed = request.Processor, OutputFormat = request.OutputFormat };
        }
        finally
        {
            DeleteTemporaryDirectory(work);
            DeleteTemporaryDirectory(pending);
        }
    }

    private Task<ProcessResult> EncodeMp3Async(string source, string destination, CancellationToken ct) =>
        processes.RunAsync(ffmpegPath,
            $"-hide_banner -loglevel error -nostdin -y -i {Quote(source)} -map 0:a:0 -vn -map_metadata -1 -c:a libmp3lame -b:a {Mp3BitrateKbps}k -id3v2_version 3 {Quote(destination)}", ct);

    internal static void EnsureGpuWasUsed(string log)
    {
        if (log.Contains("Fallback to cpu", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Falling back to CPU", StringComparison.OrdinalIgnoreCase))
            throw new AudioGpuUnavailableException("The GPU engine could not use CUDA. Choose CPU or reinstall the GPU build.");
    }

    // Two seconds of surrounding context reduce boundary artifacts; the extra samples are discarded.
    internal static IReadOnlyList<AudioSeparationChunk> PlanChunks(long frames)
    {
        if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
        const long size = 30L * SampleRate;
        const long context = 2L * SampleRate;
        var chunks = new List<AudioSeparationChunk>();
        for (long start = 0; start < frames; start += size)
        {
            long count = Math.Min(size, frames - start);
            long readStart = Math.Max(0, start - context);
            long readEnd = Math.Min(frames, start + count + context);
            chunks.Add(new(start, count, readStart, readEnd - readStart));
        }
        return chunks;
    }

    private static void WriteChunk(WaveFileReader source, string path, AudioSeparationChunk chunk, CancellationToken ct)
    {
        source.Position = chunk.ReadStartFrame * BlockAlign;
        using var output = new WaveFileWriter(path, source.WaveFormat);
        CopySamples(source, output, chunk.ReadFrameCount * BlockAlign, ct);
    }

    internal static void AppendCore(string path, WaveFileWriter writer, AudioSeparationChunk chunk, CancellationToken ct)
    {
        using var stem = new WaveFileReader(path);
        ValidateFormat(stem);
        long offset = (chunk.StartFrame - chunk.ReadStartFrame) * BlockAlign;
        long bytes = chunk.FrameCount * BlockAlign;
        if (stem.Length < offset + bytes) throw new InvalidDataException("The AI engine produced an incomplete audio stem.");
        stem.Position = offset;
        CopySamples(stem, writer, bytes, ct);
    }

    private static void ValidateFormat(WaveFileReader reader)
    {
        var format = reader.WaveFormat;
        if (format.SampleRate != SampleRate || format.Channels != 2 || format.BitsPerSample != 16 || format.Encoding != WaveFormatEncoding.Pcm)
            throw new InvalidDataException("The AI engine returned an unexpected audio format.");
    }

    private static void CopySamples(Stream input, Stream output, long bytes, CancellationToken ct)
    {
        byte[] buffer = new byte[65536];
        while (bytes > 0)
        {
            ct.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes));
            if (read == 0) throw new EndOfStreamException("Incomplete audio data.");
            output.Write(buffer, 0, read);
            bytes -= read;
        }
    }

    private static string PublishFolder(string pending, string parent, string name, CancellationToken ct)
    {
        for (int suffix = 1; ; suffix++)
        {
            ct.ThrowIfCancellationRequested();
            string destination = Path.Combine(parent, suffix == 1 ? name : $"{name} ({suffix})");
            if (Directory.Exists(destination) || File.Exists(destination)) continue;
            try { Directory.Move(pending, destination); return destination; }
            catch (IOException) when (Directory.Exists(destination) || File.Exists(destination)) { }
        }
    }

    private static string Quote(string path) => "\"" + path + "\"";
    private static void DeleteTemporaryDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}