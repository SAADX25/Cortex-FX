using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CortexFX.Core.Audio;
using CortexFX.Core.Services.Infrastructure;
using Xunit;

namespace CortexFX.Tests;

public sealed class AudioAiInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CortexFX-AiTests-" + Guid.NewGuid().ToString("N"));

    private string Prepare(byte[] expected)
    {
        string resources = Path.Combine(root, "Resources");
        Directory.CreateDirectory(resources);
        string hash = Convert.ToHexString(SHA256.HashData(expected));
        File.WriteAllText(Path.Combine(resources, "audio-resources.lock.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            packages = new[] { new { id = "test-model", gpu = false, kind = "file", url = "https://example.test/model",
                sha256 = hash, files = new[] { new { target = "model.onnx", archivePath = "", sha256 = hash, bytes = expected.Length } } } },
            licenses = Array.Empty<string>()
        }));
        return resources;
    }

    [Fact]
    public async Task VerifiedDownloadPublishesAndKeepsExistingGpu()
    {
        byte[] payload = [1, 2, 3, 4];
        string resources = Prepare(payload);
        string destination = Path.Combine(root, "Installed");
        Directory.CreateDirectory(Path.Combine(destination, "Gpu"));
        File.WriteAllText(Path.Combine(destination, "Gpu", "existing.dll"), "gpu");
        using var http = new HttpClient(new DownloadHandler(payload));
        using var processes = new ProcessManager();
        var updates = new List<AudioAiDownloadProgress>();
        await new AudioAiInstaller(processes, resources, http).InstallAsync(destination, false, new CaptureProgress(updates), default);
        Assert.Contains(updates, update => update.ReceivedBytes == payload.Length && update.TotalBytes == payload.Length);
        Assert.Contains(updates, update => update.Message.Contains("Verifying") && update.TotalBytes == null);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(destination, "model.onnx")));
        Assert.Equal("gpu", File.ReadAllText(Path.Combine(destination, "Gpu", "existing.dll")));
        Assert.Empty(Directory.GetDirectories(root, ".audio-download-*"));
    }

    [Fact]
    public async Task CorruptDownloadLeavesExistingInstallationUntouched()
    {
        string resources = Prepare([1, 2, 3, 4]);
        string destination = Path.Combine(root, "Installed");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "old.txt"), "keep");
        using var http = new HttpClient(new DownloadHandler([4, 3, 2, 1]));
        using var processes = new ProcessManager();
        await Assert.ThrowsAsync<InvalidDataException>(() => new AudioAiInstaller(processes, resources, http)
            .InstallAsync(destination, false, null, default));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "old.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "model.onnx")));
        Assert.Empty(Directory.GetDirectories(root, ".audio-download-*"));
    }

    [Fact]
    public async Task CancelledDownloadLeavesNoPublishedOrTemporaryFiles()
    {
        string resources = Prepare([1, 2, 3, 4]);
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new DownloadHandler([1, 2, 3, 4], cts));
        using var processes = new ProcessManager();
        string destination = Path.Combine(root, "Installed");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AudioAiInstaller(processes, resources, http)
            .InstallAsync(destination, false, null, cts.Token));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(root, ".audio-download-*"));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("C:\\outside.dll")]
    public void RejectsPathsOutsideInstallation(string relative) =>
        Assert.Throws<InvalidDataException>(() => AudioAiInstaller.ChildPath(root, relative));

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class CaptureProgress(List<AudioAiDownloadProgress> updates) : IProgress<AudioAiDownloadProgress>
    {
        public void Report(AudioAiDownloadProgress value) => updates.Add(value);
    }

    private sealed class DownloadHandler(byte[] payload, CancellationTokenSource? cancellation = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            cancellation?.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
