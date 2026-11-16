using System.IO;
using PdfSharp.Pdf.IO;

namespace CortexFX.Core.Services.Documents;

internal static class DocumentPdfOutput
{
    internal static void Validate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException("The document engine did not create a PDF file.");
        try
        {
            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            if (pdf.PageCount == 0) throw new InvalidDataException("The exported PDF contains no pages.");
        }
        catch (Exception ex) when (ex is not IOException && ex is not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document engine created an unreadable PDF file.", ex);
        }
    }

    internal static string ExplainOfficeFailure(string application, Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            uint code = unchecked((uint)current.HResult);
            if (code == 0x80040154)
                return $"Microsoft {application} desktop automation is not registered. Open or repair the desktop application, or install LibreOffice.";
            if (code == 0x80070005 || current is UnauthorizedAccessException)
                return $"Microsoft {application} denied access. Check that the document opens normally, use a writable output folder, and run both applications as the same Windows user. LibreOffice is an alternative.";
            if (code is 0x80080005 or 0x800706BA or 0x80010001 or 0x8001010A)
                return $"Microsoft {application} could not start or is busy. Open it normally and finish any setup or activation prompts, then retry. LibreOffice is an alternative.";
        }
        return $"Microsoft {application} did not produce a valid PDF. Open the document in the desktop application and try its Export to PDF command, or install LibreOffice.";
    }
}
