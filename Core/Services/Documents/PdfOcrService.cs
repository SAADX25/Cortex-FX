using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services.Documents;

/// <summary>Optional local Tesseract OCR. No OCR binary is bundled with Cortex FX.</summary>
public class PdfOcrService
{
    private readonly IPdfRenderService _renderer;
    private readonly IProcessManager _processes;
    private static readonly SemaphoreSlim OcrLimit = new(1);

    public PdfOcrService(IPdfRenderService renderer, IProcessManager processes)
    {
        _renderer = renderer;
        _processes = processes;
    }

    public virtual string? FindExecutable()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "tesseract.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "tesseract.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* Invalid PATH entry. */ }
        }
        return null;
    }

    public virtual async Task ConvertAsync(string pdf, string output, CancellationToken ct, IProgress<double>? progress)
    {
        string exe = FindExecutable() ?? throw new InvalidOperationException(
            "This PDF appears to contain scanned pages and OCR support is not installed.");
        await OcrLimit.WaitAsync(ct);
        string tempDir = Path.Combine(Path.GetTempPath(), "CortexFX_OCR_" + Guid.NewGuid().ToString("N"));
        try
        {
            await _renderer.RenderPdfToImagesAsync(pdf, tempDir, "png", 300, ct);
            string[] pages = Directory.GetFiles(tempDir, "*.png")
                .OrderBy(PageNumber).ToArray();
            var text = new List<string>(pages.Length);
            for (int i = 0; i < pages.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await _processes.RunAsync(exe, $"\"{pages[i]}\" stdout -l eng+ara", ct);
                    text.Add(result.StdOut);
                }
                catch (ProcessExecutionException ex)
                {
                    throw new InvalidOperationException($"OCR failed on page {i + 1}. Check that English and Arabic Tesseract data are installed.", ex);
                }
                progress?.Report(20 + 70d * (i + 1) / pages.Length);
            }
            if (text.All(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("OCR did not find editable text in this PDF.");
            WriteDocx(output, text);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            OcrLimit.Release();
        }
    }

    private static int PageNumber(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int marker = name.LastIndexOf("_Page", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 && int.TryParse(name[(marker + 5)..], out int number) ? number : int.MaxValue;
    }

    internal static void WriteDocx(string path, IReadOnlyList<string> pages)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var body = new XElement(w + "body");
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) body.Add(new XElement(w + "p", new XElement(w + "r", new XElement(w + "br", new XAttribute(w + "type", "page")))));
            foreach (string line in pages[i].Split('\n'))
                body.Add(new XElement(w + "p", new XElement(w + "r", new XElement(w + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), line.TrimEnd('\r')))));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "[Content_Types].xml", new XDocument(new XElement(ct + "Types",
            new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ct + "Override", new XAttribute("PartName", "/word/document.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")))));
        Write(zip, "_rels/.rels", new XDocument(new XElement(r + "Relationships",
            new XElement(r + "Relationship", new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                new XAttribute("Target", "word/document.xml")))));
        Write(zip, "word/document.xml", new XDocument(new XElement(w + "document", body)));
    }

    private static void Write(ZipArchive zip, string name, XDocument document)
    {
        using var stream = zip.CreateEntry(name).Open();
        document.Save(stream);
    }
}
