using System.IO.Compression;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Documents;
using Xunit;

namespace CortexFX.Tests;

public class PdfToWordTests
{
    [Fact]
    public void OcrOutputIsEditableDocxWithPageBreaks()
    {
        using var temp = new TempDocx();
        PdfOcrService.WriteDocx(temp.Path, ["Hello", "مرحبا"]);
        PdfToWordService.ValidateDocx(temp.Path);
        using var zip = ZipFile.OpenRead(temp.Path);
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        string xml = reader.ReadToEnd();
        Assert.Contains("مرحبا", xml);
        Assert.Contains("page", xml);
    }

    [Fact]
    public async Task OcrRendersAt300DpiAndProducesEditableDocx()
    {
        using var temp = new TempDocx();
        var renderer = new OcrRendererFake();
        var engine = new OcrEngineFake(renderer, new OcrProcessesFake());
        await engine.ConvertAsync("input.pdf", temp.Path, CancellationToken.None, null);
        Assert.Equal(300, renderer.Dpi);
        Assert.True(PdfToWordService.IsValidDocxPackage(temp.Path));
        Assert.True(PdfToWordService.HasMeaningfulEditableText(temp.Path));
    }

    [Fact]
    public async Task TextPdfUsesWord()
    {
        using var temp = new TempDocx();
        var office = new OfficeFake();
        var ocr = new OcrFake();
        await new PdfToWordService(new AnalysisFake(true), ocr, office)
            .ConvertAsync("input.pdf", temp.Path);
        Assert.Equal(1, office.Calls);
        Assert.Equal(0, ocr.Calls);
        Assert.True(File.Exists(temp.Path));
    }

    [Fact]
    public async Task ValidImageOnlyWordOutputFallsBackToOcr()
    {
        using var temp = new TempDocx();
        var office = new OfficeFake { ImageOnly = true };
        var ocr = new OcrFake();
        await new PdfToWordService(new AnalysisFake(true), ocr, office)
            .ConvertAsync("input.pdf", temp.Path);
        Assert.Equal(1, office.Calls);
        Assert.Equal(1, ocr.Calls);
        Assert.True(PdfToWordService.HasMeaningfulEditableText(temp.Path));
    }

    [Fact]
    public async Task ImageOnlyWordOutputWithoutOcrExplainsWhatHappened()
    {
        using var temp = new TempDocx();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PdfToWordService(new AnalysisFake(true), new OcrFake { Available = false },
                new OfficeFake { ImageOnly = true }).ConvertAsync("input.pdf", temp.Path));
        Assert.Contains("scanned or image-based", ex.Message);
        Assert.Contains("Install/configure the optional OCR engine", ex.Message);
        Assert.False(File.Exists(temp.Path));
    }

    [Fact]
    public async Task BrokenWordPackageFailsWithoutCallingOcr()
    {
        using var temp = new TempDocx();
        var ocr = new OcrFake();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PdfToWordService(new AnalysisFake(true), ocr, new OfficeFake { Invalid = true })
                .ConvertAsync("input.pdf", temp.Path));
        Assert.Contains("DOCX failed validation", ex.Message);
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public void ImageOnlyDocxIsStructurallyValidButHasNoEditableText()
    {
        using var temp = new TempDocx();
        TempDocx.WriteImageOnly(temp.Path);
        Assert.True(PdfToWordService.IsValidDocxPackage(temp.Path));
        Assert.False(PdfToWordService.HasMeaningfulEditableText(temp.Path));
    }

    [Fact]
    public void TextInTextboxCountsAsEditable()
    {
        using var temp = new TempDocx();
        TempDocx.WriteWithTextboxText(temp.Path);
        Assert.True(PdfToWordService.IsValidDocxPackage(temp.Path));
        Assert.True(PdfToWordService.HasMeaningfulEditableText(temp.Path));
    }

    [Fact]
    public void DrawingTextCountsAsEditable()
    {
        using var temp = new TempDocx();
        TempDocx.WriteWithDrawingText(temp.Path);
        Assert.True(PdfToWordService.HasMeaningfulEditableText(temp.Path));
    }

    [Fact]
    public void AnalysisRequiresTextOnMostPages()
    {
        Assert.False(PdfAnalysisService.HasUsableText(["", "", "Short caption", ""]));
        Assert.True(PdfAnalysisService.HasUsableText(["A normal page with many words and selectable text."]));
    }

    [Fact]
    public async Task ScannedPdfUsesOcr()
    {
        using var temp = new TempDocx();
        var office = new OfficeFake();
        var ocr = new OcrFake();
        await new PdfToWordService(new AnalysisFake(false), ocr, office)
            .ConvertAsync("input.pdf", temp.Path);
        Assert.Equal(0, office.Calls);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task ScannedPdfWithoutOcrShowsUsefulMessage()
    {
        using var temp = new TempDocx();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PdfToWordService(new AnalysisFake(false), new OcrFake { Available = false }, new OfficeFake())
                .ConvertAsync("input.pdf", temp.Path));
        Assert.Contains("scanned or image-based", ex.Message);
        Assert.Contains("optional OCR engine", ex.Message);
    }

    [Fact]
    public async Task WordConversionFailureFallsBackToOcr()
    {
        using var temp = new TempDocx();
        var ocr = new OcrFake();
        await new PdfToWordService(new AnalysisFake(true), ocr, new OfficeFake { ThrowOnConvert = true })
            .ConvertAsync("input.pdf", temp.Path);
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task InvalidOutputIsRejected()
    {
        using var temp = new TempDocx();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PdfToWordService(new AnalysisFake(false), new OcrFake { Invalid = true }, new OfficeFake())
                .ConvertAsync("input.pdf", temp.Path));
        Assert.False(File.Exists(temp.Path));
    }

    [Fact]
    public async Task CancellationAvoidsAllEngines()
    {
        using var temp = new TempDocx();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfToWordService(new AnalysisFake(true), new OcrFake(), new OfficeFake())
                .ConvertAsync("input.pdf", temp.Path, cts.Token));
    }
}

internal sealed class AnalysisFake(bool hasText) : PdfAnalysisService
{
    public override Task<bool> HasUsableTextAsync(string path, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(hasText); }
}

internal sealed class OcrRendererFake : IPdfRenderService
{
    public int Dpi { get; private set; }
    public Task RenderPdfToImagesAsync(string pdf, string output, string format, int dpi = 150,
        CancellationToken ct = default, IProgress<double>? progress = null)
    {
        Dpi = dpi;
        Directory.CreateDirectory(output);
        File.WriteAllBytes(System.IO.Path.Combine(output, "input_Page1.png"), [1]);
        return Task.CompletedTask;
    }
}

internal sealed class OcrProcessesFake : FakeProcesses
{
    public override Task<ProcessResult> RunAsync(string e, string a, CancellationToken ct = default) =>
        Task.FromResult(new ProcessResult(0, "Editable OCR text", ""));
}

internal sealed class OcrEngineFake(IPdfRenderService renderer, IProcessManager processes)
    : PdfOcrService(renderer, processes)
{
    public override string? FindExecutable() => "fake.exe";
}

internal sealed class OcrFake() : PdfOcrService(new FakeEngines(), new FakeProcesses())
{
    public bool Available { get; set; } = true;
    public bool Invalid { get; set; }
    public int Calls { get; private set; }
    public override string? FindExecutable() => Available ? "fake.exe" : null;
    public override Task ConvertAsync(string pdf, string output, CancellationToken ct, IProgress<double>? progress)
    {
        ct.ThrowIfCancellationRequested();
        if (!Available) throw new InvalidOperationException("This PDF appears to contain scanned pages and OCR support is not installed.");
        Calls++;
        if (Invalid) File.WriteAllText(output, "invalid");
        else TempDocx.WriteValid(output);
        return Task.CompletedTask;
    }
}

internal sealed class OfficeFake : IOfficeInteropService
{
    public bool Invalid { get; set; }
    public bool ImageOnly { get; set; }
    public bool ThrowOnConvert { get; set; }
    public int Calls { get; private set; }
    public bool IsOfficeInstalled() => true;
    public Task ConvertPdfToWordAsync(string input, string output, CancellationToken ct = default, IProgress<double>? p = null)
    {
        ct.ThrowIfCancellationRequested(); Calls++;
        if (ThrowOnConvert) throw new InvalidOperationException("Word conversion failed.");
        if (Invalid) File.WriteAllText(output, "invalid");
        else if (ImageOnly) TempDocx.WriteImageOnly(output);
        else TempDocx.WriteValid(output);
        return Task.CompletedTask;
    }
    public Task ConvertToPdfAsync(string a, string b, int q = 1, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public Task ConvertWordToPowerPointAsync(string a, string b, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
    public Task ConvertPdfToPowerPointAsync(string a, string b, CancellationToken ct = default, IProgress<double>? p = null) => Task.CompletedTask;
}

internal sealed class TempDocx : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".docx");
    public static void WriteValid(string path)
        => Write(path, "<w:p><w:r><w:t>Editable text</w:t></w:r></w:p>");
    public static void WriteImageOnly(string path)
        => Write(path, "<w:p><w:r><w:drawing/></w:r></w:p>");
    public static void WriteWithTextboxText(string path)
        => Write(path, "<w:p><w:r><w:drawing><w:txbxContent><w:p><w:r><w:t>X</w:t></w:r></w:p></w:txbxContent></w:drawing></w:r></w:p>");
    public static void WriteWithDrawingText(string path)
        => Write(path, "<w:p><w:r><w:drawing><a:t xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">Editable</a:t></w:drawing></w:r></w:p>");
    private static void Write(string path, string body)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var types = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
            types.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        using (var rels = new StreamWriter(zip.CreateEntry("_rels/.rels").Open()))
            rels.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>");
    }
    public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
}
