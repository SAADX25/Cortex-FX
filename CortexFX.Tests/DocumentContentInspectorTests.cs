using System.IO.Compression;
using CortexFX.Core.Services.Documents;
using CortexFX.Models;
using PdfSharp.Drawing;
using Xunit;

namespace CortexFX.Tests;

public sealed class DocumentContentInspectorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CortexFX-ContentTests-" + Guid.NewGuid().ToString("N"));
    public DocumentContentInspectorTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("")]
    [InlineData("<w:p><w:r><w:t>  &#x200B; </w:t></w:r><w:r><w:br/></w:r></w:p><w:sectPr/>")]
    public async Task FormattedBlankWordIsRejected(string body)
    {
        string path = Word(body);
        await Assert.ThrowsAsync<EmptyDocumentException>(() => new DocumentContentInspector().EnsureContentAsync(path));
    }

    [Theory]
    [InlineData("<w:p><w:r><w:t>!</w:t></w:r></w:p>")]
    [InlineData("<w:p><w:r><w:drawing/></w:r></w:p>")]
    [InlineData("<w:tbl><w:tr><w:tc><w:p/></w:tc></w:tr></w:tbl>")]
    public async Task ShortTextImagesAndTablesAreNotBlankWord(string body) =>
        await new DocumentContentInspector().EnsureContentAsync(Word(body));

    [Fact]
    public async Task StrictWordAndHeaderContentAreRecognized()
    {
        string path = Word("<w:p><w:r><w:t>مرحبا</w:t></w:r></w:p>", strict: true);
        await new DocumentContentInspector().EnsureContentAsync(path);
        path = Word("");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            Add(zip, "word/header1.xml", "<w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:r><w:t>Header</w:t></w:r></w:p></w:hdr>");
        await new DocumentContentInspector().EnsureContentAsync(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<p:sp><p:nvSpPr><p:nvPr><p:ph/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t> </a:t></a:r></a:p></p:txBody></p:sp>")]
    public async Task BlankSlidesAndEmptyPlaceholdersAreRejected(string shapes) =>
        await Assert.ThrowsAsync<EmptyDocumentException>(() => new DocumentContentInspector().EnsureContentAsync(Presentation(shapes)));

    [Theory]
    [InlineData("<p:sp><p:txBody><a:p><a:r><a:t>نص</a:t></a:r></a:p></p:txBody></p:sp>")]
    [InlineData("<p:pic/>")]
    [InlineData("<p:graphicFrame/>")]
    [InlineData("<p:sp><p:spPr><a:prstGeom prst=\"rect\"/></p:spPr></p:sp>")]
    public async Task TextImagesChartsAndShapesArePresentationContent(string shapes) =>
        await new DocumentContentInspector().EnsureContentAsync(Presentation(shapes));

    [Fact]
    public async Task PresentationWithNoSlidesIsEmptyEvenIfUnusedAssetsRemain()
    {
        string path = Presentation("<p:pic/>");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("ppt/presentation.xml")!.Delete();
            Add(zip, "ppt/presentation.xml", "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:sldIdLst/></p:presentation>");
        }
        await Assert.ThrowsAsync<EmptyDocumentException>(() => new DocumentContentInspector().EnsureContentAsync(path));
    }

    [Fact]
    public async Task BlankPdfIsRejectedButDrawingOnLaterPageIsNot()
    {
        string path = Path.Combine(root, "pages.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument()) { pdf.AddPage(); pdf.AddPage(); pdf.Save(path); }
        await Assert.ThrowsAsync<EmptyDocumentException>(() => new DocumentContentInspector().EnsureContentAsync(path));
        await Assert.ThrowsAsync<EmptyDocumentException>(() => new PdfAnalysisService().HasUsableTextAsync(path));
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            pdf.AddPage();
            var page = pdf.AddPage();
            using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawLine(XPens.Black, 20, 20, 150, 150);
            pdf.Save(path);
        }
        await new DocumentContentInspector().EnsureContentAsync(path);
        Assert.False(await new PdfAnalysisService().HasUsableTextAsync(path));
    }

    [Fact]
    public async Task ScannedImagePdfIsNotMistakenForAnEmptyPdf()
    {
        string path = Path.Combine(root, "scan.pdf");
        using var bitmap = new System.Drawing.Bitmap(8, 8);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(System.Drawing.Color.Navy);
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        png.Position = 0;
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            var page = pdf.AddPage();
            using var image = XImage.FromStream(png);
            using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawImage(image, 20, 20, 100, 100);
            pdf.Save(path);
        }
        await new DocumentContentInspector().EnsureContentAsync(path);
    }

    [Theory]
    [InlineData(".docx", "pdf")]
    [InlineData(".pptx", "pdf")]
    [InlineData(".pdf", "docx")]
    [InlineData(".pdf", "pptx")]
    public async Task EmptyFilesStopBeforeAnyEngineAndPreserveExistingOutput(string ext, string target)
    {
        string source = Path.Combine(root, "empty" + ext);
        File.WriteAllBytes(source, []);
        string output = Path.Combine(root, "empty." + target);
        File.WriteAllText(output, "previous result");
        var engines = new FakeEngines { DocumentInspector = new DocumentContentInspector() };
        var result = await engines.Router.ConvertAsync(new ConversionJob
        { InputPath = source, OutputDirectory = root, TargetFormat = target, CreateSubfolder = false });
        Assert.False(result.Success);
        Assert.Equal(EmptyDocumentException.UserMessage, result.ErrorMessage);
        Assert.Null(engines.Called);
        Assert.Equal("previous result", File.ReadAllText(output));
    }

    private string Word(string body, bool strict = false)
    {
        string path = Path.Combine(root, Guid.NewGuid() + ".docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        string ns = strict ? "http://purl.oclc.org/ooxml/wordprocessingml/main" : "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Add(zip, "word/document.xml", $"<w:document xmlns:w=\"{ns}\"><w:body>{body}</w:body></w:document>");
        return path;
    }

    private string Presentation(string shapes)
    {
        string path = Path.Combine(root, Guid.NewGuid() + ".pptx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "ppt/presentation.xml", "<p:presentation xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId1\"/></p:sldIdLst></p:presentation>");
        Add(zip, "ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id=\"rId1\" Target=\"slides/slide1.xml\"/></Relationships>");
        Add(zip, "ppt/slides/slide1.xml", $"<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><p:cSld><p:spTree>{shapes}</p:spTree></p:cSld></p:sld>");
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(content); }
}
