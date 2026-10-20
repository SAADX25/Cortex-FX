using CortexFX.Models;

namespace CortexFX.Core.Interfaces;

/// <summary>
/// Conversions that need large optional desktop tools such as LibreOffice,
/// 7-Zip, or Calibre. The app can use them when installed without bundling them.
/// </summary>
public interface IOptionalConversionService
{
    bool CanConvert(string inputExtension, string targetFormat);

    /// <summary>Checks installed tools without starting a conversion process.</summary>
    string? GetUnavailableReason(string inputExtension, string targetFormat) =>
        CanConvert(inputExtension, targetFormat) ? null : "This conversion is not supported by an optional engine.";

    Task<ConversionResult> ConvertAsync(
        string inputFile,
        string outputPath,
        string inputExtension,
        string targetFormat,
        CancellationToken ct = default,
        IProgress<double>? progress = null);
}
