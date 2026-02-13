using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using CortexFX.Core.Engines;
using System.Threading;
using System.Threading.Tasks;

namespace CortexFX
{
    public static class DocumentConverter
    {
        public static async Task ConvertDocumentAsync(string inputFile, string outputFile, string targetFormat, int qualityLevel = 1, CancellationToken cancellationToken = default, IProgress<double> progress = null)
        {
            if (targetFormat.ToLower() == "pdf")
            {
                await CortexEngine.ConvertToPdfAsync(inputFile, outputFile, qualityLevel, cancellationToken, progress);
            }
            else if (targetFormat.ToLower() == "docx")
            {
                if (Path.GetExtension(inputFile).ToLower() == ".pdf")
                {
                    await CortexEngine.ConvertPdfToWordAsync(inputFile, outputFile, cancellationToken, progress);
                }
                else if (Path.GetExtension(inputFile).ToLower() == ".pptx" || Path.GetExtension(inputFile).ToLower() == ".ppt")
                {
                    // Bridge: PPT -> PDF -> Word
                    string tempPdf = Path.Combine(Path.GetDirectoryName(outputFile)!, Guid.NewGuid().ToString() + ".pdf");
                    try
                    {
                        var pdfProgress = new Progress<double>(p => progress?.Report(p * 0.5));
                        await CortexEngine.ConvertToPdfAsync(inputFile, tempPdf, qualityLevel, cancellationToken, pdfProgress);
                        
                        if (cancellationToken.IsCancellationRequested) return;
                        
                        var wordProgress = new Progress<double>(p => progress?.Report(50 + (p * 0.5)));
                        await CortexEngine.ConvertPdfToWordAsync(tempPdf, outputFile, cancellationToken, wordProgress);
                    }
                    finally
                    {
                        try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
                    }
                }
            }
            else if (targetFormat.ToLower() == "pptx")
            {
                if (Path.GetExtension(inputFile).ToLower() == ".docx" || Path.GetExtension(inputFile).ToLower() == ".doc")
                {
                     // Native Engine Bridge: Word -> PowerPoint
                     await CortexEngine.ConvertWordToPowerPointAsync(inputFile, outputFile, cancellationToken, progress);
                }
                else if (Path.GetExtension(inputFile).ToLower() == ".pdf")
                {
                     // Smart Bridge: PDF -> Word -> PowerPoint
                     await CortexEngine.ConvertPdfToPowerPointAsync(inputFile, outputFile, cancellationToken, progress);
                }
            }
        }

        public static async Task ConvertToPdfAsync(string inputFile, string outputFile, IProgress<double> progress = null)
        {
            await ConvertDocumentAsync(inputFile, outputFile, "pdf", 1, default, progress);
        }

        public static async Task ConvertPdfToOfficeAsync(string inputFile, string outputFile, string targetFormat, IProgress<double> progress = null)
        {
            await ConvertDocumentAsync(inputFile, outputFile, targetFormat, 1, default, progress);
        }

        public static async Task ConvertWordToPowerPointAsync(string inputFile, string outputFile, IProgress<double> progress = null)
        {
            await ConvertDocumentAsync(inputFile, outputFile, "pptx", 1, default, progress);
        }

        public static async Task ConvertPowerPointToWordAsync(string inputFile, string outputFile, IProgress<double> progress = null)
        {
            await ConvertDocumentAsync(inputFile, outputFile, "docx", 1, default, progress);
        }
    }
}
