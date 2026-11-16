using System.IO.Compression;
using CortexFX.Core.Services.Documents;
using CortexFX.Core.Services.Infrastructure;
using Xunit;

namespace CortexFX.Tests;

public sealed class OfficePdfIntegrationTests
{
    [OfficeFact]
    public async Task RealWordExportsAReadablePdf()
    {
        string work = Path.Combine(Path.GetTempPath(), "CortexFX-RealWord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string source = Path.Combine(work, "sample.docx");
            using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
            {
                Add(archive, "[Content_Types].xml", """
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
                    """);
                Add(archive, "_rels/.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
                    """);
                Add(archive, "word/document.xml", """
                    <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Cortex FX Word PDF verification</w:t></w:r></w:p><w:sectPr/></w:body></w:document>
                    """);
            }
            await ExportAndCheck(source, Path.Combine(work, "word.pdf"));
            string blank = Path.Combine(work, "blank.docx");
            File.Copy(source, blank);
            using (var archive = ZipFile.Open(blank, ZipArchiveMode.Update))
            {
                archive.GetEntry("word/document.xml")!.Delete();
                Add(archive, "word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p/><w:sectPr/></w:body></w:document>");
            }
            using var processes = new ProcessManager();
            await Assert.ThrowsAsync<EmptyDocumentException>(() => new OfficeInteropService(processes).ConvertToPdfAsync(blank, Path.Combine(work, "blank.pdf")));
            Assert.False(File.Exists(Path.Combine(work, "blank.pdf")));
        }
        finally { Directory.Delete(work, true); }
    }

    [OfficeFact]
    public async Task RealPowerPointExportsAReadOnlyPresentationToPdf()
    {
        string work = Path.Combine(Path.GetTempPath(), "CortexFX-RealPowerPoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string source = Path.Combine(work, "sample.pptx");
        try
        {
            await Sta(() =>
            {
                var prior = System.Diagnostics.Process.GetProcessesByName("POWERPNT");
                var priorIds = prior.Select(process => process.Id).ToHashSet();
                foreach (var process in prior) process.Dispose();
                NetOffice.PowerPointApi.Application? app = null;
                NetOffice.PowerPointApi.Presentation? deck = null;
                bool owned = false;
                try
                {
                    app = new NetOffice.PowerPointApi.Application();
                    int pid = OfficeInteropService.GetOfficePid(app);
                    Assert.True(pid > 0, "PowerPoint process ownership could not be determined.");
                    owned = !priorIds.Contains(pid);
                    app.DisplayAlerts = NetOffice.PowerPointApi.Enums.PpAlertLevel.ppAlertsNone;
                    deck = app.Presentations.Add(NetOffice.OfficeApi.Enums.MsoTriState.msoFalse);
                    using var slide = deck.Slides.Add(1, NetOffice.PowerPointApi.Enums.PpSlideLayout.ppLayoutText);
                    deck.SaveAs(Path.Combine(work, "blank.pptx"), NetOffice.PowerPointApi.Enums.PpSaveAsFileType.ppSaveAsOpenXMLPresentation);
                    slide.Shapes[1].TextFrame.TextRange.Text = "Cortex FX PowerPoint PDF verification";
                    deck.SaveAs(source, NetOffice.PowerPointApi.Enums.PpSaveAsFileType.ppSaveAsOpenXMLPresentation);
                }
                finally
                {
                    if (deck != null) { try { deck.Close(); } finally { deck.Dispose(); } }
                    if (app != null) { try { if (owned) app.Quit(); } finally { app.Dispose(); } }
                }
            });
            File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
            await ExportAndCheck(source, Path.Combine(work, "powerpoint.pdf"));
            using var processes = new ProcessManager();
            await Assert.ThrowsAsync<EmptyDocumentException>(() => new OfficeInteropService(processes).ConvertToPdfAsync(
                Path.Combine(work, "blank.pptx"), Path.Combine(work, "blank.pdf")));
            Assert.False(File.Exists(Path.Combine(work, "blank.pdf")));
        }
        finally
        {
            if (File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal);
            Directory.Delete(work, true);
        }
    }

    private static async Task ExportAndCheck(string source, string output)
    {
        using var processes = new ProcessManager();
        var service = new OfficeInteropService(processes);
        await service.ConvertToPdfAsync(source, output);
        DocumentPdfOutput.Validate(output);
        Assert.True(new FileInfo(output).Length > 1000);
        using var pdf = PdfiumViewer.PdfDocument.Load(output);
        Assert.Equal(1, pdf.PageCount);
        Assert.Contains("Cortex", pdf.GetPdfText(0));
    }

    private static void Add(ZipArchive archive, string name, string content)
    { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }

    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completion.Task;
    }

    private sealed class OfficeFactAttribute : FactAttribute
    {
        public OfficeFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CORTEXFX_TEST_OFFICE") != "1")
                Skip = "Set CORTEXFX_TEST_OFFICE=1 on a Windows desktop with Word and PowerPoint installed.";
        }
    }
}
