using System.Net.Http;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace CortexFX.Core.Services.Infrastructure;

public sealed record UpdateRelease(Version Version, string Tag, Uri InstallerUrl, long Size, string? Sha256);

/// <summary>Checks the official stable GitHub release and stages its Windows installer.</summary>
public sealed class UpdateService
{
    private static readonly Uri LatestReleaseApi = new("https://api.github.com/repos/SAADX25/Cortex-FX/releases/latest");
    private readonly HttpClient _client;

    public UpdateService(HttpClient client)
    {
        _client = client;
    }

    public async Task<UpdateRelease?> CheckAsync(Version installedVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("CortexFX-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var release = document.RootElement;

        string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latestVersion))
            throw new InvalidDataException("The latest GitHub release has an invalid version tag.");

        if (latestVersion <= installedVersion)
            return null;

        string expectedName = $"CortexFX_Setup_v{latestVersion.ToString(3)}.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), expectedName, StringComparison.OrdinalIgnoreCase))
                continue;

            string? downloadUrl = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/SAADX25/Cortex-FX/releases/download/", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)), expectedName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release installer URL is not from the official Cortex FX repository.");

            string? digest = asset.TryGetProperty("digest", out var digestNode) ? digestNode.GetString() : null;
            string? sha256 = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                ? digest[7..] : null;
            if (sha256 != null && (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("The release installer checksum is invalid.");

            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0)
                throw new InvalidDataException("The release installer has an invalid size.");
            return new UpdateRelease(latestVersion, tag, uri, size, sha256);
        }

        throw new InvalidDataException($"Release {tag} does not include the Windows installer ({expectedName}).");
    }

    public async Task<string> DownloadInstallerAsync(UpdateRelease release, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string folder = Path.Combine(Path.GetTempPath(), "CortexFX", "Updates");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, $"CortexFX_Setup_v{release.Version.ToString(3)}.exe");
        string partial = destination + ".download";
        try
        {
            using var response = await _client.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    written += count;
                    progress?.Report(Math.Min(100, written * 100.0 / release.Size));
                }
                if (written != release.Size)
                    throw new InvalidDataException("The downloaded installer is incomplete.");
            }

            await using (var file = File.OpenRead(partial))
            {
                if (file.ReadByte() != 'M' || file.ReadByte() != 'Z')
                    throw new InvalidDataException("The downloaded file is not a Windows installer.");
                if (release.Sha256 != null)
                {
                    file.Position = 0;
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                    if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The downloaded installer failed its SHA-256 checksum.");
                }
            }

            File.Move(partial, destination, true);
            return destination;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }
}
