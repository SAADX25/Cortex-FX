using System;
using System.IO;
using Spire.Doc;
using Spire.Presentation;
using Spire.Xls;

using Spire.Pdf;
using Spire.Pdf.Graphics;

namespace CortexFX
{
    public static class DocumentConverter
    {
        public static void ConvertToPdf(string inputFile, string outputFile)
        {
            string extension = Path.GetExtension(inputFile).ToLower();

            switch (extension)
            {
                case ".docx":
                case ".doc":
                    ConvertWordToPdf(inputFile, outputFile);
                    break;
                case ".xlsx":
                case ".xls":
                    ConvertExcelToPdf(inputFile, outputFile);
                    break;
                case ".pptx":
                case ".ppt":
                    ConvertPptToPdf(inputFile, outputFile);
                    break;
                default:
                    throw new NotSupportedException($"The file format '{extension}' is not supported for document conversion.");
            }
        }

        public static void ConvertPdfToOffice(string inputFile, string outputFile, string targetFormat)
        {
            PdfDocument document = new PdfDocument();
            document.LoadFromFile(inputFile);

            switch (targetFormat.ToLower())
            {
                case "docx":
                    document.SaveToFile(outputFile, Spire.Pdf.FileFormat.DOCX);
                    break;
                case "xlsx":
                    document.SaveToFile(outputFile, Spire.Pdf.FileFormat.XLSX);
                    break;
                case "pptx":
                    document.SaveToFile(outputFile, Spire.Pdf.FileFormat.PPTX);
                    break;
                default:
                    throw new NotSupportedException($"The target format '{targetFormat}' is not supported for PDF conversion.");
            }
            
            document.Close();
        }

        private static void ConvertWordToPdf(string inputFile, string outputFile)
        {
            Document document = new Document();
            document.LoadFromFile(inputFile);
            document.SaveToFile(outputFile, Spire.Doc.FileFormat.PDF);
            document.Close();
        }

        private static void ConvertExcelToPdf(string inputFile, string outputFile)
        {
            Workbook workbook = new Workbook();
            workbook.LoadFromFile(inputFile);
            workbook.ConverterSetting.SheetFitToPage = true;
            workbook.SaveToFile(outputFile, Spire.Xls.FileFormat.PDF);
            workbook.Dispose();
        }

        private static void ConvertPptToPdf(string inputFile, string outputFile)
        {
            Presentation presentation = new Presentation();
            presentation.LoadFromFile(inputFile);
            presentation.SaveToFile(outputFile, Spire.Presentation.FileFormat.PDF);
            presentation.Dispose();
        }
    }
}
