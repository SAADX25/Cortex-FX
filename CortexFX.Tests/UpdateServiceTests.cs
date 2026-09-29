using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CortexFX.Core.Services.Infrastructure;
using Xunit;

namespace CortexFX.Tests;

public class UpdateServiceTests
{
    private const string InstallerUrl = "https://github.com/SAADX25/Cortex-FX/releases/download/v1.8.0/CortexFX_Setup_v1.8.0.exe";

    [Fact]
    public async Task NewerReleaseSelectsOfficialInstaller()
    {
        using var client = new HttpClient(new FakeHandler(_ => JsonResponse(ReleaseJson(InstallerUrl))));
        var release = await new UpdateService(client).CheckAsync(new Version(1, 7, 0, 0));
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 8, 0), release.Version);
        Assert.Equal(InstallerUrl, release.InstallerUrl.ToString());
    }

    [Fact]
    public async Task CurrentReleaseNeedsNoDownload()
    {
        using var client = new HttpClient(new FakeHandler(_ => JsonResponse(ReleaseJson(InstallerUrl))));
        Assert.Null(await new UpdateService(client).CheckAsync(new Version(1, 8, 0, 0)));
    }

    [Fact]
    public async Task RejectsInstallerOutsideOfficialRepository()
    {
        using var client = new HttpClient(new FakeHandler(_ => JsonResponse(
            ReleaseJson("https://example.com/CortexFX_Setup_v1.8.0.exe"))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateService(client).CheckAsync(new Version(1, 7, 0)));
    }

    [Fact]
    public async Task DownloadVerifiesSizeAndChecksumBeforeStaging()
    {
        byte[] installer = [0x4D, 0x5A, 0x01, 0x02];
        string digest = Convert.ToHexString(SHA256.HashData(installer));
        using var client = new HttpClient(new FakeHandler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? JsonResponse(ReleaseJson(InstallerUrl, installer.Length, digest))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) }));
        var service = new UpdateService(client);
        var release = await service.CheckAsync(new Version(1, 7, 0));
        Assert.NotNull(release);
        string path = await service.DownloadInstallerAsync(release);
        try
        {
            Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ReleaseJson(string url, int size = 4, string? digest = null) =>
        $$"""
        {"tag_name":"v1.8.0","assets":[{"name":"CortexFX_Setup_v1.8.0.exe","browser_download_url":"{{url}}","size":{{size}},"digest":"sha256:{{digest ?? new string('0', 64)}}"}]}
        """;

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
