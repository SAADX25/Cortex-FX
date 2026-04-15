namespace CortexFX.Core.Interfaces;

/// <summary>
/// Contract for all Microsoft Office COM interop operations via NetOffice.
/// Replaces the static CortexEngine class.
/// </summary>
public interface IOfficeInteropService
{
    /// <summary>Check if Microsoft Office is installed on this machine.</summary>
    bool IsOfficeInstalled();

    /// <summary>Convert Word/Excel/PowerPoint documents to PDF.</summary>
    Task ConvertToPdfAsync(string inputFile, string outputFile, int qualityLevel = 1,
                           CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>Convert a PDF to a Word document (opens PDF in Word, saves as DOCX).</summary>
    Task ConvertPdfToWordAsync(string inputFile, string outputFile,
                               CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>Convert a Word document to a PowerPoint presentation.</summary>
    Task ConvertWordToPowerPointAsync(string inputFile, string outputFile,
                                      CancellationToken ct = default, IProgress<double>? progress = null);

    /// <summary>Convert a PDF to PowerPoint (via PDF→Word→PowerPoint bridge).</summary>
    Task ConvertPdfToPowerPointAsync(string inputFile, string outputFile,
                                     CancellationToken ct = default, IProgress<double>? progress = null);
}
