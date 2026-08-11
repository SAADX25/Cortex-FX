namespace CortexFX.Core.Interfaces;

/// <summary>
/// Microsoft Office conversions through NetOffice (Word / Excel / PowerPoint).
/// </summary>
public interface IOfficeInteropService
{
    /// <summary>True if Office looks installed on this PC.</summary>
    bool IsOfficeInstalled();

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
