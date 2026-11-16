using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Audio;

public sealed record AudioAiDownloadProgress(string Message, long ReceivedBytes = 0, long? TotalBytes = null);

/// <summary>Installs pinned, verified optional AI resources in the user's local data folder.</summary>
public sealed class AudioAiInstaller(IProcessManager processes, string resourcesDirectory, HttpClient? httpClient = null)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(2) };

    internal static string ChildPath(string root, string relative)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid AI resource path.");
        return path;
    }

    internal static async Task VerifyAsync(string path, long bytes, string hash, CancellationToken ct)
    {
        if (new FileInfo(path).Length != bytes) throw new InvalidDataException("AI resource size mismatch.");
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AI resource checksum mismatch.");
    }

    public Task InstallAsync(string destination, bool gpu, IProgress<AudioAiDownloadProgress>? progress, CancellationToken ct) =>
        Task.Run(() => InstallCoreAsync(destination, gpu, progress, ct), ct);

    private async Task InstallCoreAsync(string destination, bool gpu, IProgress<AudioAiDownloadProgress>? progress, CancellationToken ct)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(resourcesDirectory, "audio-resources.lock.json"), ct));
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported AI manifest.");
        string parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        string work = Path.Combine(parent, ".audio-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string staged = Path.Combine(work, "ready");
        Directory.CreateDirectory(staged);
        try
        {
            // Existing resources are retained until every requested file has been verified.
            foreach (var package in manifest.RootElement.GetProperty("packages").EnumerateArray())
            {
                if (package.GetProperty("gpu").GetBoolean() && !gpu) continue;
                var files = package.GetProperty("files").EnumerateArray().ToArray();
                var missing = new List<JsonElement>();
                foreach (var file in files)
                {
                    string relative = file.GetProperty("target").GetString()!;
                    string bundled = ChildPath(Path.Combine(resourcesDirectory, "AudioSeparation"), relative);
                    string installed = ChildPath(destination, relative);
                    string? source = File.Exists(installed) ? installed : File.Exists(bundled) ? bundled : null;
                    if (source != null)
                    {
                        try
                        {
                            await VerifyFileAsync(source, file, ct);
                            Copy(source, ChildPath(staged, relative));
                            continue;
                        }
                        catch (InvalidDataException) { }
                    }
                    missing.Add(file);
                }
                if (missing.Count == 0) continue;
                string id = package.GetProperty("id").GetString()!;
                string archive = ChildPath(work, id + ".download");
                var uri = new Uri(package.GetProperty("url").GetString()!);
                if (uri.Scheme != "https") throw new InvalidDataException("AI downloads require HTTPS.");
                progress?.Report(new("Connecting to download…"));
                using (var response = await (httpClient ?? Http).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;
                    progress?.Report(new("Downloading AI files", 0, totalBytes));
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = File.Create(archive);
                    byte[] buffer = new byte[131072];
                    long total = 0;
                    long lastUpdate = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        total += read;
                        if (total - lastUpdate >= 1048576)
                        {
                            progress?.Report(new("Downloading AI files", total, totalBytes));
                            lastUpdate = total;
                        }
                    }
                    progress?.Report(new("Downloading AI files", total, totalBytes));
                }
                await VerifyAsync(archive, new FileInfo(archive).Length, package.GetProperty("sha256").GetString()!, ct);
                progress?.Report(new("Verifying and installing AI files…"));
                string kind = package.GetProperty("kind").GetString()!;
                string extracted = Path.Combine(work, "extract-" + id);
                Directory.CreateDirectory(extracted);
                if (kind == "tar.bz2")
                {
                    var entries = missing.Select(f => f.GetProperty("archivePath").GetString()!).ToArray();
                    foreach (string entry in entries) _ = ChildPath(extracted, entry);
                    string args = "-xjf " + Quote(archive) + " -C " + Quote(extracted) + " -- " + string.Join(" ", entries.Select(Quote));
                    await processes.RunAsync(Path.Combine(Environment.SystemDirectory, "tar.exe"), args, ct);
                }
                else if (kind == "zip")
                {
                    using var zip = ZipFile.OpenRead(archive);
                    foreach (var file in missing)
                    {
                        ct.ThrowIfCancellationRequested();
                        string entryName = file.GetProperty("archivePath").GetString()!;
                        var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException("Missing AI archive entry.");
                        string output = ChildPath(extracted, entryName);
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        entry.ExtractToFile(output);
                    }
                }
                else if (kind != "file") throw new InvalidDataException("Unsupported AI archive.");
                foreach (var file in missing)
                {
                    string source = kind == "file" ? archive : ChildPath(extracted, file.GetProperty("archivePath").GetString()!);
                    await VerifyFileAsync(source, file, ct);
                    Copy(source, ChildPath(staged, file.GetProperty("target").GetString()!));
                }
                File.Delete(archive);
            }
            foreach (string key in gpu ? new[] { "licenses", "gpuLicenses" } : new[] { "licenses" })
                foreach (var license in manifest.RootElement.GetProperty(key).EnumerateArray())
                {
                    string relative = license.GetString()!;
                    Copy(ChildPath(Path.Combine(resourcesDirectory, "AudioSeparation"), relative), ChildPath(staged, relative));
                }
            if (gpu)
                foreach (var file in manifest.RootElement.GetProperty("microsoftRuntime").EnumerateArray())
                {
                    string relative = file.GetProperty("target").GetString()!;
                    string source = await FindMicrosoftRuntimeAsync(relative, file, ct);
                    Copy(source, ChildPath(staged, relative));
                }
            ct.ThrowIfCancellationRequested();
            string previous = Path.Combine(work, "previous");
            bool hadPrevious = Directory.Exists(destination);
            if (hadPrevious) Directory.Move(destination, previous);
            bool retainedGpu = false;
            try
            {
                // A CPU repair keeps the existing GPU add-on without copying gigabytes.
                if (!gpu && Directory.Exists(Path.Combine(previous, "Gpu")))
                {
                    Directory.Move(Path.Combine(previous, "Gpu"), Path.Combine(staged, "Gpu"));
                    retainedGpu = true;
                }
                Directory.Move(staged, destination);
            }
            catch
            {
                if (retainedGpu) Directory.Move(Path.Combine(staged, "Gpu"), Path.Combine(previous, "Gpu"));
                if (hadPrevious) Directory.Move(previous, destination);
                throw;
            }
        }
        finally
        {
            // work is a fresh generated child, never an input path.
            Directory.Delete(work, true);
        }
    }

    private static Task VerifyFileAsync(string path, JsonElement file, CancellationToken ct) =>
        VerifyAsync(path, file.GetProperty("bytes").GetInt64(), file.GetProperty("sha256").GetString()!, ct);

    private async Task<string> FindMicrosoftRuntimeAsync(string relative, JsonElement file, CancellationToken ct)
    {
        string name = Path.GetFileName(relative);
        foreach (string candidate in new[] { ChildPath(Path.Combine(resourcesDirectory, "AudioSeparation"), relative),
            Path.Combine(resourcesDirectory, name), Path.Combine(Environment.SystemDirectory, name) })
        {
            if (!File.Exists(candidate)) continue;
            try { await VerifyFileAsync(candidate, file, ct); return candidate; }
            catch (InvalidDataException) { }
            // The developer restoration tool also accepts newer signed Microsoft runtimes.
            string literal = "'" + candidate.Replace("'", "''") + "'";
            string script = "$ErrorActionPreference='Stop'; $p=" + literal + "; " +
                "$v=[Diagnostics.FileVersionInfo]::GetVersionInfo($p); " +
                "if ($v.FileMajorPart -lt 14 -or ($v.FileMajorPart -eq 14 -and $v.FileMinorPart -lt 44)) { exit 1 }; " +
                "$s=Get-AuthenticodeSignature -LiteralPath $p; " +
                "if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { exit 1 }";
            try
            {
                await processes.RunAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), ct);
                return candidate;
            }
            catch (ProcessExecutionException) { }
        }
        throw new FileNotFoundException("Microsoft Visual C++ x64 runtime is required for GPU setup: " + name);
    }
    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, true);
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
}
