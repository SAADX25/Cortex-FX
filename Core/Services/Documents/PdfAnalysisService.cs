using System.IO;

namespace CortexFX.Core.Services.Documents;

public class PdfAnalysisService
{
    public virtual Task<bool> HasUsableTextAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        using var pdf = PdfiumViewer.PdfDocument.Load(path);
        if (pdf.PageCount == 0) throw new InvalidDataException("The PDF has no pages.");
        var pageText = new List<string>(pdf.PageCount);
        for (int page = 0; page < pdf.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            pageText.Add(pdf.GetPdfText(page));
        }
        return HasUsableText(pageText);
    }, ct);

    internal static bool HasUsableText(IReadOnlyList<string> pages)
    {
        if (pages.Count == 0) return false;
        int textPages = pages.Count(page =>
        {
            int letters = page.Count(char.IsLetterOrDigit);
            int words = page.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Count(word => word.Any(char.IsLetterOrDigit));
            return letters >= 30 || (letters >= 16 && words >= 5);
        });
        // A little selectable text on one page should not classify a mostly scanned PDF as text.
        return textPages * 4 >= pages.Count * 3;
    }
}
