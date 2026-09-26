using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Infrastructure;

namespace CortexFX.Core.Services.Documents;

public sealed class PdfToWordService : IPdfToWordService
{
    private const string ScannedPdfMessage =
        "This PDF appears to contain scanned or image-based pages. Microsoft Word could not produce editable text. Install/configure the optional OCR engine to convert this PDF to editable DOCX.";
    private readonly PdfAnalysisService _analysis;
    private readonly PdfOcrService _ocr;
    private readonly IOfficeInteropService _office;

    public PdfToWordService(PdfAnalysisService analysis, PdfOcrService ocr, IOfficeInteropService office)
    {
        _analysis = analysis;
        _ocr = ocr;
        _office = office;
    }

    public async Task ConvertAsync(string inputFile, string outputFile, CancellationToken ct = default,
                                   IProgress<double>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        bool hasText = await _analysis.HasUsableTextAsync(inputFile, ct);
        string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputFile))!,
            ".cortexfx_" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            if (hasText)
            {
                if (!_office.IsOfficeInstalled())
                    throw new InvalidOperationException("Microsoft Word is required for this PDF conversion.");
                try
                {
                    await _office.ConvertPdfToWordAsync(inputFile, temp, ct, progress);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ConsoleLogger.Error("PDF", ex.ToString());
                    if (_ocr.FindExecutable() == null)
                        throw new InvalidOperationException("Word could not convert this PDF, and OCR support is not installed.", ex);
                    await RunOcrAsync(inputFile, temp, ct, progress);
                    PublishValidatedOutput(temp, outputFile, progress);
                    return;
                }

                ct.ThrowIfCancellationRequested();
                // A structurally valid Word result can still consist entirely of page images.
                // That is an OCR case, not a corrupt DOCX package.
                EnsureValidPackage(temp);
                if (HasMeaningfulEditableText(temp))
                {
                    PublishValidatedOutput(temp, outputFile, progress);
                    return;
                }
                ConsoleLogger.Warning("PDF", "Word produced a DOCX without editable text; trying OCR.");
                if (_ocr.FindExecutable() == null)
                    throw new InvalidOperationException(ScannedPdfMessage);
            }
            else if (_ocr.FindExecutable() == null)
                throw new InvalidOperationException(ScannedPdfMessage);

            await RunOcrAsync(inputFile, temp, ct, progress);
            PublishValidatedOutput(temp, outputFile, progress);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private async Task RunOcrAsync(string inputFile, string temp, CancellationToken ct, IProgress<double>? progress)
    {
        if (File.Exists(temp)) File.Delete(temp);
        await _ocr.ConvertAsync(inputFile, temp, ct, progress);
        ct.ThrowIfCancellationRequested();
        EnsureValidPackage(temp);
        if (!HasMeaningfulEditableText(temp))
            throw new InvalidDataException("OCR did not produce editable text in the DOCX.");
    }

    private static void PublishValidatedOutput(string temp, string outputFile, IProgress<double>? progress)
    {
        File.Move(temp, outputFile, true);
        progress?.Report(100);
    }

    internal static void ValidateDocx(string path)
    {
        EnsureValidPackage(path);
        if (!HasMeaningfulEditableText(path))
            throw new InvalidDataException("DOCX has no editable text.");
    }

    private static void EnsureValidPackage(string path)
    {
        if (!IsValidDocxPackage(path))
            throw new InvalidDataException("The PDF was converted, but the generated DOCX failed validation.");
    }

    internal static bool IsValidDocxPackage(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var types = zip.GetEntry("[Content_Types].xml");
            var relationships = zip.GetEntry("_rels/.rels");
            if (types == null || relationships == null) return false;
            XDocument contentTypes;
            using (var typesStream = types.Open())
                contentTypes = XDocument.Load(typesStream);
            if (contentTypes.Root?.Name.LocalName != "Types" ||
                !contentTypes.Descendants().Any(element =>
                    element.Name.LocalName == "Override" &&
                    (string?)element.Attribute("PartName") == "/word/document.xml" &&
                    (string?)element.Attribute("ContentType") ==
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))
                return false;
            XDocument packageRelationships;
            using (var relationshipsStream = relationships.Open())
                packageRelationships = XDocument.Load(relationshipsStream);
            if (packageRelationships.Root?.Name.LocalName != "Relationships" ||
                !packageRelationships.Descendants().Any(element =>
                    element.Name.LocalName == "Relationship" &&
                    ((string?)element.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true &&
                    ((string?)element.Attribute("Target"))?.TrimStart('/') == "word/document.xml"))
                return false;
            var document = zip.GetEntry("word/document.xml");
            if (document == null) return false;
            using var stream = document.Open();
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var xml = XDocument.Load(stream);
            if (xml.Root?.Name != w + "document" || xml.Root.Element(w + "body") == null) return false;
            foreach (var entry in zip.Entries.Where(e => IsTextPart(e.FullName) && e.FullName != "word/document.xml"))
            {
                using var partStream = entry.Open();
                XDocument.Load(partStream);
            }
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (System.Xml.XmlException) { return false; }
        catch (IOException) { return false; }
    }

    internal static bool HasMeaningfulEditableText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace m = "http://schemas.openxmlformats.org/officeDocument/2006/math";
        foreach (var entry in zip.Entries)
        {
            if (!IsTextPart(entry.FullName)) continue;
            using var stream = entry.Open();
            var xml = XDocument.Load(stream);
            // Includes runs in hyperlinks/content controls/text boxes, plus editable drawing and math text.
            // Field instructions and deleted text are deliberately excluded: they are not visible content.
            if (xml.Descendants().Any(t =>
                (t.Name == w + "t" || t.Name == a + "t" || t.Name == m + "t") &&
                t.Value.Any(char.IsLetterOrDigit)))
                return true;
        }
        return false;
    }

    private static bool IsTextPart(string name) =>
        name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase) ||
        (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
         (name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
          name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase)));
}
