using CortexFX.Models;

namespace CortexFX.Core.Interfaces;

/// <summary>
/// Central routing engine that determines which service handles a given conversion.
/// Replaces the legacy routing block in MainWindow.ConvertButton_Click().
/// </summary>
public interface IConversionRouter
{
    /// <summary>
    /// Get supported output formats for a given input file extension.
    /// </summary>
    IReadOnlyList<string> GetSupportedFormats(string inputExtension);

    /// <summary>
    /// Execute the correct engine pipeline for a conversion job.
    /// Returns a result indicating success or failure.
    /// </summary>
    Task<ConversionResult> ConvertAsync(ConversionJob job, CancellationToken ct = default,
                                         IProgress<double>? progress = null);
}
