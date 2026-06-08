using CortexFX.Models;

namespace CortexFX.Core.Interfaces;

/// <summary>
/// Conversions that need large optional desktop tools such as LibreOffice,
/// 7-Zip, or Calibre. The app can use them when installed without bundling them.
/// </summary>
public interface IOptionalConversionService
{
    bool CanConvert(string inputExtension, string targetFormat);

    Task<ConversionResult> ConvertAsync(
        string inputFile,
        string outputPath,
        string inputExtension,
        string targetFormat,
        CancellationToken ct = default,
        IProgress<double>? progress = null);
}
