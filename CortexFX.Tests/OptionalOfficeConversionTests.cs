using System.Text.RegularExpressions;
using CortexFX.Core.Interfaces;
using CortexFX.Core.Services.Documents;
using Xunit;

namespace CortexFX.Tests;

public sealed class OptionalOfficeConversionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CortexFX-OptionalOfficeTests-" + Guid.NewGuid().ToString("N"));
    public OptionalOfficeConversionTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "LibreOffice", "program"));
        File.WriteAllText(Path.Combine(root, "LibreOffice", "program", "soffice.exe"), "not executed");
        File.WriteAllText(Path.Combine(root, "document.docx"), "test source");
    }

    [Fact]
    public async Task SuccessfulExitWithoutOutputCannotReuseAnOldPdf()
    {
        var process = new OfficeProcess(false);
        var service = new OptionalConversionService(new FakeConfig(root), process);
        string output = Path.Combine(root, "document.pdf");
        File.WriteAllText(output, "keep previous output");
        var result = await service.ConvertAsync(Path.Combine(root, "document.docx"), output, ".docx", "pdf");
        Assert.False(result.Success);
        Assert.Equal("keep previous output", File.ReadAllText(output));
        Assert.False(Directory.Exists(process.WorkDirectories.Single()));
    }

    [Fact]
    public async Task EachExportUsesItsOwnWritableProfileAndPublishesOnlyAReadablePdf()
    {
        var process = new OfficeProcess(true);
        var service = new OptionalConversionService(new FakeConfig(root), process);
        string output = Path.Combine(root, "renamed.pdf");
        for (int i = 0; i < 2; i++)
        {
            var result = await service.ConvertAsync(Path.Combine(root, "document.docx"), output, ".docx", "pdf");
            Assert.True(result.Success, result.ErrorMessage);
            DocumentPdfOutput.Validate(output);
        }
        Assert.Equal(2, process.WorkDirectories.Distinct().Count());
        Assert.All(process.WorkDirectories, directory => Assert.False(Directory.Exists(directory)));
        Assert.All(process.Arguments, args => Assert.Contains("-env:UserInstallation=\"file:///", args));
    }

    public void Dispose() => Directory.Delete(root, true);

    private sealed class OfficeProcess(bool createOutput) : FakeProcesses
    {
        internal List<string> WorkDirectories { get; } = [];
        internal List<string> Arguments { get; } = [];
        public override Task<ProcessResult> RunAsync(string executable, string arguments, CancellationToken ct = default)
        {
            Arguments.Add(arguments);
            string output = Regex.Match(arguments, "--outdir \"([^\"]+)\"").Groups[1].Value;
            Assert.True(Path.IsPathFullyQualified(output));
            WorkDirectories.Add(Path.GetDirectoryName(output)!);
            if (createOutput)
            {
                using var pdf = new PdfSharp.Pdf.PdfDocument();
                pdf.AddPage();
                pdf.Save(Path.Combine(output, "document.pdf"));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}
