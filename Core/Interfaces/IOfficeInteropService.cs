namespace CortexFX.Core.Interfaces;

/// <summary>
/// Microsoft Office conversions through NetOffice (Word / Excel / PowerPoint).
/// </summary>
public interface IOfficeInteropService
{
    /// <summary>True if Office looks installed on this PC.</summary>
    bool IsOfficeInstalled();

    /// <summary>Checks Word, Excel, or PowerPoint individually without launching Office.</summary>
    bool IsApplicationInstalled(string application) => IsOfficeInstalled();

    /// <summary>Save a Word-compatible document as DOCX, ODT, RTF, or Unicode TXT.</summary>
    Task ConvertWordDocumentAsync(string inputFile, string outputFile, string targetFormat,
        CancellationToken ct = default, IProgress<double>? progress = null) =>
        throw new NotSupportedException("Word document export is not implemented by this service.");

    /// <summary>Word / Excel / PowerPoint → PDF.</summary>
    Task ConvertToPdfAsync(string inputFile, string outputFile, int qualityLevel = 1,
                           CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>PDF → DOCX (opens the PDF in Word, then saves).</summary>
    Task ConvertPdfToWordAsync(string inputFile, string outputFile,
                               CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>Word → PowerPoint.</summary>
    Task ConvertWordToPowerPointAsync(string inputFile, string outputFile,
                                      CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>PDF → PowerPoint (goes through Word in between).</summary>
    Task ConvertPdfToPowerPointAsync(string inputFile, string outputFile,
                                     CancellationToken ct = default, IProgress<double>? progress = null);
}
