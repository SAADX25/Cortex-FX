using CortexFX.Core.Interfaces;

namespace CortexFX.Models;

/// <summary>
/// Represents a single conversion job to be executed by the ConversionRouter.
/// Encapsulates all parameters that were previously scattered as local variables
/// in ConvertButton_Click().
/// </summary>
public class ConversionJob
{
    /// <summary>Full path to the input file.</summary>
    public required string InputPath { get; init; }

    /// <summary>Directory for output files.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Target format (lowercase, no dot), e.g. "pdf", "mp4", "jpg".</summary>
    public required string TargetFormat { get; init; }

    /// <summary>Quality level (0-100). Interpretation depends on the engine.</summary>
    public double QualityLevel { get; init; } = 75;

    /// <summary>Whether to create a "Cortex FX" subfolder in the output directory.</summary>
    public bool CreateSubfolder { get; init; } = true;

    /// <summary>Image-specific conversion options (null for non-image conversions).</summary>
    public ImageConversionOptions? ImageOptions { get; init; }
}

/// <summary>
/// Result of a conversion operation.
/// </summary>
public class ConversionResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? ErrorMessage { get; init; }

    public static ConversionResult Ok(string outputPath) => new()
    {
        Success = true,
        OutputPath = outputPath
    };

    public static ConversionResult Fail(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };
}
