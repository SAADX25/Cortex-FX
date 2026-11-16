using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace CortexFX.Core.Services.Documents;

public sealed class EmptyDocumentException : InvalidOperationException
{
    public const string UserMessage = "الملف فارغ، لا يوجد محتوى للتحويل.";
    public EmptyDocumentException() : base(UserMessage) { }
}

/// <summary>Rejects empty inputs, including formatted blank pages, without treating scans as missing text.</summary>
public class DocumentContentInspector
{
    public virtual Task EnsureContentAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length == 0) throw new EmptyDocumentException();
        bool hasContent = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => HasWordContent(path, ct),
            ".pptx" => HasPowerPointContent(path, ct),
            ".pdf" => HasPdfContent(path, ct),
            ".txt" => HasText(File.ReadAllText(path)),
            // Legacy formats are checked after their native engine renders them.
            _ => true
        };
        if (!hasContent) throw new EmptyDocumentException();
    }, ct);

    private static bool HasText(string text) => text.Any(c => !char.IsWhiteSpace(c) && !char.IsControl(c) &&
        char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format);

    private static XDocument Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var xml = XDocument.Load(stream);
        // Office supports both transitional and strict OOXML namespace names.
        foreach (var element in xml.Root!.DescendantsAndSelf())
        {
            string ns = element.Name.NamespaceName;
            if (ns.StartsWith("http://purl.oclc.org/ooxml/", StringComparison.Ordinal))
                element.Name = XName.Get(element.Name.LocalName, StandardNamespace(ns));
            foreach (var attribute in element.Attributes().Where(a => a.Name.NamespaceName.StartsWith("http://purl.oclc.org/ooxml/", StringComparison.Ordinal)).ToArray())
            {
                element.SetAttributeValue(XName.Get(attribute.Name.LocalName, StandardNamespace(attribute.Name.NamespaceName)), attribute.Value);
                attribute.Remove();
            }
        }
        return xml;
    }

    private static string StandardNamespace(string ns) => ns.Replace("http://purl.oclc.org/ooxml/", "http://schemas.openxmlformats.org/")
        .Replace("/wordprocessingml/main", "/wordprocessingml/2006/main")
        .Replace("/presentationml/main", "/presentationml/2006/main")
        .Replace("/drawingml/main", "/drawingml/2006/main")
        .Replace("/officeDocument/relationships", "/officeDocument/2006/relationships")
        .Replace("/officeDocument/math", "/officeDocument/2006/math");

    private static bool HasWordContent(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.GetEntry("word/document.xml") == null) throw new InvalidDataException("The Word document is invalid.");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace m = "http://schemas.openxmlformats.org/officeDocument/2006/math";
        foreach (var part in zip.Entries.Where(entry => entry.FullName == "word/document.xml" ||
            entry.FullName == "word/footnotes.xml" || entry.FullName == "word/endnotes.xml" ||
            (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
             (entry.FullName.StartsWith("word/header") || entry.FullName.StartsWith("word/footer")))))
        {
            ct.ThrowIfCancellationRequested();
            var xml = Read(part);
            if (xml.Descendants().Any(e => (e.Name == w + "t" || e.Name == a + "t" || e.Name == m + "t") && HasText(e.Value))) return true;
            if (xml.Descendants().Any(e => e.Name == w + "drawing" || e.Name == w + "pict" || e.Name == w + "object" ||
                e.Name == w + "tbl" || e.Name == w + "altChunk" || e.Name == w + "sym" || e.Name == w + "fldSimple" || e.Name == m + "oMath")) return true;
        }
        return false;
    }

    private static bool HasPowerPointContent(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var presentation = zip.GetEntry("ppt/presentation.xml") ?? throw new InvalidDataException("The presentation is invalid.");
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var ids = Read(presentation).Descendants(p + "sldId").Select(e => (string?)e.Attribute(r + "id")).ToHashSet();
        if (ids.Count == 0) return false;
        var relationships = zip.GetEntry("ppt/_rels/presentation.xml.rels") ?? throw new InvalidDataException("Presentation relationships are missing.");
        foreach (var relationship in Read(relationships).Root!.Elements().Where(e => ids.Contains((string?)e.Attribute("Id"))))
        {
            ct.ThrowIfCancellationRequested();
            string target = (string?)relationship.Attribute("Target") ?? "";
            string name = ResolvePart("ppt/presentation.xml", target);
            var slide = zip.GetEntry(name) ?? throw new InvalidDataException("A presentation slide is missing.");
            var xml = Read(slide);
            if ((string?)xml.Root?.Attribute("show") is "0" or "false") continue;
            if (SlideHasContent(xml, false)) return true;
            // Background artwork and non-placeholder text on layouts/masters are real slide content.
            foreach (var layout in RelatedParts(zip, name, "/slideLayout"))
            {
                if (SlideHasContent(Read(layout), true)) return true;
                foreach (var master in RelatedParts(zip, layout.FullName, "/slideMaster"))
                    if (SlideHasContent(Read(master), true)) return true;
            }
        }
        return false;
    }

    private static bool SlideHasContent(XDocument xml, bool inherited)
    {
        XNamespace p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        if (xml.Descendants().Any(e => e.Name == p + "pic" || e.Name == p + "graphicFrame" || e.Name == p + "cxnSp" || e.Name == a + "blip")) return true;
        if (!inherited && xml.Descendants(p + "bg").Any()) return true;
        if (inherited && xml.Descendants(p + "bg").Descendants(a + "srgbClr")
            .Any(e => !string.Equals((string?)e.Attribute("val"), "FFFFFF", StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var shape in xml.Descendants(p + "sp"))
        {
            bool placeholder = shape.Descendants(p + "ph").Any();
            if (!(inherited && placeholder) && shape.Descendants(a + "t").Any(t => HasText(t.Value))) return true;
            bool textBox = shape.Descendants(p + "cNvSpPr").Any(e => (string?)e.Attribute("txBox") is "1" or "true");
            if (!placeholder && !textBox && shape.Descendants().Any(e => e.Name == a + "prstGeom" || e.Name == a + "custGeom")) return true;
            if (!placeholder && shape.Elements(p + "spPr").Descendants(a + "solidFill").Any()) return true;
        }
        return false;
    }

    private static string ResolvePart(string part, string target) =>
        Uri.UnescapeDataString(new Uri(new Uri("https://package/" + part), target).AbsolutePath.TrimStart('/'));

    private static IEnumerable<ZipArchiveEntry> RelatedParts(ZipArchive zip, string part, string relation)
    {
        string relName = part[..(part.LastIndexOf('/') + 1)] + "_rels/" + Path.GetFileName(part) + ".rels";
        var rels = zip.GetEntry(relName);
        if (rels == null) yield break;
        foreach (var rel in Read(rels).Root!.Elements().Where(e => ((string?)e.Attribute("Type"))?.EndsWith(relation, StringComparison.Ordinal) == true &&
            (string?)e.Attribute("TargetMode") != "External"))
        {
            var entry = zip.GetEntry(ResolvePart(part, (string?)rel.Attribute("Target") ?? ""));
            if (entry != null) yield return entry;
        }
    }

    internal static bool HasPdfContent(string path, CancellationToken ct)
    { using var pdf = PdfiumViewer.PdfDocument.Load(path); return HasPdfContent(pdf, ct); }

    internal static bool HasPdfContent(PdfiumViewer.PdfDocument pdf, CancellationToken ct)
    {
        for (int page = 0; page < pdf.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (HasText(pdf.GetPdfText(page))) return true;
            var size = pdf.PageSizes[page];
            double scale = 1024 / Math.Max(size.Width, size.Height);
            int width = Math.Max(1, (int)(size.Width * scale));
            int height = Math.Max(1, (int)(size.Height * scale));
            using var image = pdf.Render(page, width, height, 96, 96, PdfiumViewer.PdfRenderFlags.Annotations);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            { graphics.Clear(Color.White); graphics.DrawImage(image, 0, 0, width, height); }
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * data.Stride + x * 4;
                        if (bytes[i] < 250 || bytes[i + 1] < 250 || bytes[i + 2] < 250) return true;
                    }
            }
            finally { bitmap.UnlockBits(data); }
        }
        return false;
    }
}
