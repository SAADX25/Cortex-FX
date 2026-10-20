using System.IO;

namespace CortexFX.Core.Radial;

/// <summary>Stage every engine in an isolated sibling directory, then publish without overwriting.</summary>
public sealed class RadialOutput : IDisposable
{
    private readonly string _source;
    private bool _moved;
    public string WorkDirectory { get; }

    public RadialOutput(string source)
    {
        _source = Path.GetFullPath(source);
        WorkDirectory = Path.Combine(Path.GetDirectoryName(_source)!, ".cortex-wheel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkDirectory);
    }

    public string FilePath(string extension) => Path.Combine(WorkDirectory, "result." + extension.TrimStart('.'));

    public string CommitFile(string path, string extension, string tag = "")
    {
        EnsureContained(path);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException("The conversion produced no output data.");
        foreach (string destination in Candidates("." + extension.TrimStart('.'), tag))
        {
            try
            {
                using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { continue; }
            try { File.Move(path, destination, true); return destination; }
            catch { File.Delete(destination); throw; }
        }
        throw new IOException("Unable to reserve an output name.");
    }

    public IReadOnlyList<string> CommitDirectory(string tag)
    {
        if (!Directory.EnumerateFiles(WorkDirectory, "*", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("The conversion produced no output files.");
        foreach (string destination in Candidates("", tag))
        {
            try
            {
                Directory.Move(WorkDirectory, destination);
                _moved = true;
                return Directory.GetFiles(destination, "*", SearchOption.AllDirectories);
            }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
        }
        throw new IOException("Unable to reserve an output folder.");
    }

    private IEnumerable<string> Candidates(string extension, string tag)
    {
        string stem = Path.GetFileNameWithoutExtension(_source) + (tag.Length > 0 ? "_" + tag : "");
        for (int index = 0; index < 100000; index++)
            yield return Path.Combine(Path.GetDirectoryName(_source)!, stem + (index > 0 ? $" ({index})" : "") + extension);
    }

    private void EnsureContained(string path)
    {
        string relative = Path.GetRelativePath(WorkDirectory, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("The engine returned an unexpected output location.");
    }

    public void Dispose()
    {
        if (!_moved && Directory.Exists(WorkDirectory))
        {
            // Only the exclusively named staging directory owned by this instance.
            EnsureContained(WorkDirectory);
            Directory.Delete(WorkDirectory, true);
        }
    }
}
