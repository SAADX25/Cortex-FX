using CortexFX.Models;

namespace CortexFX.Core.Interfaces;

/// <summary>
/// Decides which engine runs a conversion job.
/// </summary>
public interface IConversionRouter
{
    /// <summary>Output formats available for this input extension.</summary>
    IReadOnlyList<string> GetSupportedFormats(string inputExtension);

    /// <summary>Run one conversion job.</summary>
    Task<ConversionResult> ConvertAsync(ConversionJob job, CancellationToken ct = default,
                                         IProgress<double>? progress = null);
}
