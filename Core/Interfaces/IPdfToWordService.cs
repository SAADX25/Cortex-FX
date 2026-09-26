namespace CortexFX.Core.Interfaces;

public interface IPdfToWordService
{
    Task ConvertAsync(string inputFile, string outputFile, CancellationToken ct = default,
                      IProgress<double>? progress = null);
}
