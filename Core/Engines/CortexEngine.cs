using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NetOffice.WordApi;
using NetOffice.WordApi.Enums;
using NetOffice.PowerPointApi;
using NetOffice.PowerPointApi.Enums;
using NetOffice.ExcelApi;
using NetOffice.ExcelApi.Enums;
using Word = NetOffice.WordApi;
using PowerPoint = NetOffice.PowerPointApi;
using Excel = NetOffice.ExcelApi;
using Task = System.Threading.Tasks.Task;

namespace CortexFX.Core.Engines
{
    public static class CortexEngine
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cortex_log.txt");
        private static readonly List<int> _managedPids = new List<int>();

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}");
            }
            catch { }
        }

        // Pre-Launch Cleanup: Kill zombies and clear temp files
        public static void PreLaunchCleanup()
        {
            Log("Performing Pre-Launch Cleanup...");
            try
            {
                // 1. Kill Zombie Office Processes (Aggressive Boot Clean)
                string[] procNames = { "WINWORD", "POWERPNT", "EXCEL" };
                foreach (var name in procNames)
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName(name);
                    foreach (var p in procs)
                    {
                        // Check if it has a visible window. If not, it's likely a zombie from a crash.
                        // Also check for empty title which usually indicates a background automation instance.
                        if (string.IsNullOrEmpty(p.MainWindowTitle))
                        {
                            try 
                            { 
                                p.Kill(); 
                                Log($"Boot Cleanup: Killed zombie {name} (PID: {p.Id})");
                            } 
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Pre-Launch Cleanup Error: {ex.Message}");
            }
        }

        // Global Cleanup triggered on App Exit
        public static void GlobalCleanup()
        {
            try
            {
                lock (_managedPids)
                {
                    foreach (int pid in _managedPids)
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(pid);
                            if (!proc.HasExited)
                            {
                                proc.Kill();
                                Log($"Global Cleanup: Killed process {pid}");
                            }
                        }
                        catch { /* Already gone */ }
                    }
                    _managedPids.Clear();
                }
            }
            catch { }
        }
        
        // Helper to track PID from COM object if possible, or just rely on aggressive cleanup
        // Getting PID from NetOffice/COM object can be tricky (GetWindowThreadProcessId).
        // Instead, we will wrap logic to capture PID after creation if we can, or just be very careful.
        // Actually, simpler approach: Use the aggressive cleanup helpers.
        
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private static int GetProcessId(object application)
        {
            try
            {
                // Try to get HWND from application object (Word/Excel/PPT usually have Hwnd property)
                // NetOffice proxies might need casting or dynamic
                dynamic app = application;
                int hwnd = app.Hwnd;
                GetWindowThreadProcessId((IntPtr)hwnd, out uint pid);
                return (int)pid;
            }
            catch 
            { 
                return 0; 
            }
        }

        public static bool IsOfficeInstalled()
        {
            try
            {
                Type? officeType = Type.GetTypeFromProgID("Word.Application");
                return officeType != null;
            }
            catch
            {
                return false;
            }
        }

        public static async Task ConvertToPdfAsync(string inputFile, string outputFile, int qualityLevel = 1, CancellationToken cancellationToken = default, IProgress<double> progress = null)
        {
            await Task.Run(() =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Log($"Starting PDF conversion for: {inputFile}");
                string extension = Path.GetExtension(inputFile).ToLower();

                if (!IsOfficeInstalled())
                {
                    Log("Office not installed.");
                    throw new Exception("Microsoft Office is required for the Native Engine. Please install Office.");
                }

                if (cancellationToken.IsCancellationRequested) return;
                progress?.Report(10); // Initializing

                switch (extension)
                {
                    case ".docx":
                    case ".doc":
                        ConvertWordToPdf(inputFile, outputFile, qualityLevel);
                        break;
                    case ".xlsx":
                    case ".xls":
                        ConvertExcelToPdf(inputFile, outputFile, qualityLevel);
                        break;
                    case ".pptx":
                    case ".ppt":
                        ConvertPowerPointToPdf(inputFile, outputFile, qualityLevel);
                        break;
                    default:
                        throw new NotSupportedException($"Format '{extension}' is not supported by Cortex Engine.");
                }
                
                stopwatch.Stop();
                Log($"Conversion successful. Time: {stopwatch.Elapsed.TotalSeconds:F2} seconds.");
                progress?.Report(100); // Done
            }, cancellationToken);
        }

        public static async Task ConvertPdfToWordAsync(string inputFile, string outputFile, CancellationToken cancellationToken = default, IProgress<double> progress = null)
        {
            await Task.Run(() =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Log($"Starting PDF to Word conversion for: {inputFile}");
                if (!IsOfficeInstalled()) throw new Exception("Microsoft Office is required.");

                Word.Application app = null;
                Word.Document doc = null;
                try
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    progress?.Report(10); // Initializing app

                    app = new Word.Application();
                    app.Visible = false;
                    app.DisplayAlerts = WdAlertLevel.wdAlertsNone;

                    Log("Opening PDF in Word...");
                    progress?.Report(30); // Opening
                    doc = app.Documents.Open(inputFile, false, true);
                    
                    if (cancellationToken.IsCancellationRequested) return;
                    progress?.Report(60); // Processing

                    Log("Saving as DOCX...");
                    doc.SaveAs2(outputFile, WdSaveFormat.wdFormatXMLDocument);
                    
                    doc.Close(false);
                    doc = null; // Mark as null after close
                    
                    stopwatch.Stop();
                    Log($"Conversion successful. Time: {stopwatch.Elapsed.TotalSeconds:F2} seconds.");
                    progress?.Report(100);
                }
                catch (Exception ex)
                {
                    Log($"Error: {ex.Message}");
                    throw new Exception($"Cortex Engine Error (PDF->Word): {ex.Message}");
                }
                finally
                {
                    CleanupWord(app, doc);
                }
            }, cancellationToken);
        }

        public static async Task ConvertPdfToPowerPointAsync(string inputFile, string outputFile, CancellationToken cancellationToken = default, IProgress<double> progress = null)
        {
             await Task.Run(async () =>
             {
                 var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                 Log($"Starting PDF to PPTX conversion for: {inputFile}");
                 if (!IsOfficeInstalled()) throw new Exception("Microsoft Office is required.");

                 string tempDocx = Path.Combine(Path.GetDirectoryName(outputFile)!, Guid.NewGuid().ToString() + ".docx");
                 
                 try
                 {
                     if (cancellationToken.IsCancellationRequested) return;
                     progress?.Report(10); // Starting
                     
                     // Stage 1: PDF -> Word
                     // Create a sub-progress for this stage (0-50%)
                     var wordProgress = new Progress<double>(p => progress?.Report(10 + (p * 0.4)));
                     await ConvertPdfToWordAsync(inputFile, tempDocx, cancellationToken, wordProgress);
                     
                     progress?.Report(50); // Word conversion done
                     
                     // Wait for file release
                     int retries = 0;
                     while (!File.Exists(tempDocx) && retries < 20)
                     {
                         Thread.Sleep(200);
                         retries++;
                     }
                     Thread.Sleep(1000); 

                     if (cancellationToken.IsCancellationRequested) return;
                     progress?.Report(60); // Starting PPT import

                     // Stage 2: Word -> PPTX
                     // Create a sub-progress for this stage (60-100%)
                     var pptProgress = new Progress<double>(p => progress?.Report(60 + (p * 0.4)));
                     await ConvertWordToPowerPointAsync(tempDocx, outputFile, cancellationToken, pptProgress);
                     
                     stopwatch.Stop();
                     Log($"Conversion successful. Time: {stopwatch.Elapsed.TotalSeconds:F2} seconds.");
                     progress?.Report(100);
                 }
                 finally
                 {
                     try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
                 }
             }, cancellationToken);
        }

        public static async Task ConvertWordToPowerPointAsync(string inputFile, string outputFile, CancellationToken cancellationToken = default, IProgress<double> progress = null)
        {
            await Task.Run(() =>
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Log($"Starting Word to PPTX conversion for: {inputFile}");
                PowerPoint.Application app = null;
                Word.Application wordApp = null;
                Word.Document sourceDoc = null;
                PowerPoint.Presentation pres = null;

                try
                {
                    progress?.Report(10); // Initializing
                    
                    try 
                    {
                        app = new PowerPoint.Application();
                        // 1. Silent Mode Fix: Do NOT set Visible property directly on Application (causes PropertySet crash).
                        // Instead, use WithWindow parameter in Presentations.Open or Add.
                        
                        // Disable alerts to prevent blocking
                        app.DisplayAlerts = PpAlertLevel.ppAlertsNone;
                    }
                    catch (Exception initEx)
                    {
                        Log($"PowerPoint Init Error: {initEx.Message}");
                        throw new Exception($"Failed to initialize PowerPoint: {initEx.Message}");
                    }

                    // Create new presentation (WithWindow=msoFalse to keep it hidden)
                    pres = app.Presentations.Add(NetOffice.OfficeApi.Enums.MsoTriState.msoFalse);

                    wordApp = new Word.Application();
                    // 2. Word Silence (Visible property is safe on Word)
                    wordApp.Visible = false;
                    wordApp.DisplayAlerts = WdAlertLevel.wdAlertsNone;
                    
                    sourceDoc = wordApp.Documents.Open(inputFile, false, true);

                    int paragraphCount = sourceDoc.Paragraphs.Count;
                    int currentParaIndex = 1;
                    const int PARAS_PER_SLIDE = 7;
                    
                    progress?.Report(30); // Reading content

                    while (currentParaIndex <= paragraphCount)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        
                        // Report progress based on paragraphs processed
                        double percent = 30 + (60.0 * currentParaIndex / paragraphCount);
                        progress?.Report(percent);

                        PowerPoint.Slide slide = pres.Slides.Add(pres.Slides.Count + 1, PpSlideLayout.ppLayoutText);
                        slide.Shapes[1].TextFrame.TextRange.Text = "Document Content";

                        string slideText = "";
                        for (int i = 0; i < PARAS_PER_SLIDE && currentParaIndex <= paragraphCount; i++)
                        {
                            string text = sourceDoc.Paragraphs[currentParaIndex].Range.Text.Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                slideText += text + "\r\n";
                            }
                            currentParaIndex++;
                        }

                        var bodyShape = slide.Shapes[2];
                        bodyShape.TextFrame.TextRange.Text = slideText;
                        bodyShape.TextFrame.TextRange.Font.Name = "Calibri";
                        bodyShape.TextFrame.TextRange.Font.Size = 20;
                        bodyShape.TextFrame.TextRange.ParagraphFormat.Alignment = PpParagraphAlignment.ppAlignLeft;
                    }

                    sourceDoc.Close(false);
                    sourceDoc = null;

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        progress?.Report(95); // Saving
                        string absOutputPath = Path.GetFullPath(outputFile);
                        pres.SaveAs(absOutputPath, PpSaveAsFileType.ppSaveAsOpenXMLPresentation);
                    }
                    pres.Close();
                    pres = null;
                    
                    stopwatch.Stop();
                    Log($"Conversion successful. Time: {stopwatch.Elapsed.TotalSeconds:F2} seconds.");
                    progress?.Report(100);
                }
                catch (Exception ex)
                {
                    Log($"Error (Word->PPT): {ex.Message}");
                    throw new Exception($"Cortex Engine Error (Word->PPT): {ex.Message}");
                }
                finally
                {
                    CleanupWord(wordApp, sourceDoc);
                    CleanupPowerPoint(app, pres);
                }
            }, cancellationToken);
        }

        private static void ConvertWordToPdf(string inputFile, string outputFile, int qualityLevel)
        {
            Word.Application app = null;
            Word.Document doc = null;
            try
            {
                Log("Starting Word->PDF native conversion...");
                app = new Word.Application();
                app.Visible = false;
                app.DisplayAlerts = WdAlertLevel.wdAlertsNone;

                doc = app.Documents.Open(inputFile, false, true);
                
                WdExportOptimizeFor optimization = WdExportOptimizeFor.wdExportOptimizeForPrint;
                if (qualityLevel == 0) optimization = WdExportOptimizeFor.wdExportOptimizeForOnScreen;

                string absOutputPath = Path.GetFullPath(outputFile);
                Log($"Exporting to: {absOutputPath}");
                doc.ExportAsFixedFormat(absOutputPath, WdExportFormat.wdExportFormatPDF, false, optimization);
                doc.Close(false);
                doc = null;
            }
            catch (Exception ex)
            {
                Log($"Word->PDF Failed: {ex.Message}");
                throw new Exception($"Cortex Engine Error (Word->PDF): {ex.Message}");
            }
            finally
            {
                CleanupWord(app, doc);
            }
        }

        private static void ConvertExcelToPdf(string inputFile, string outputFile, int qualityLevel)
        {
            Excel.Application app = null;
            Excel.Workbook wb = null;
            try
            {
                Log("Starting Excel->PDF native conversion...");
                app = new Excel.Application();
                app.Visible = false;
                app.DisplayAlerts = false;

                wb = app.Workbooks.Open(inputFile, 0, true);

                XlFixedFormatQuality quality = XlFixedFormatQuality.xlQualityStandard;
                if (qualityLevel == 0) quality = XlFixedFormatQuality.xlQualityMinimum;

                string absOutputPath = Path.GetFullPath(outputFile);
                Log($"Exporting to: {absOutputPath}");
                wb.ExportAsFixedFormat(XlFixedFormatType.xlTypePDF, absOutputPath, quality, true, false);
                wb.Close(false);
                wb = null;
            }
            catch (Exception ex)
            {
                Log($"Excel->PDF Failed: {ex.Message}");
                throw new Exception($"Cortex Engine Error (Excel->PDF): {ex.Message}");
            }
            finally
            {
                CleanupExcel(app, wb);
            }
        }

        private static void ConvertPowerPointToPdf(string inputFile, string outputFile, int qualityLevel)
        {
            Log("Starting PPT->PDF native conversion...");
            KillGhostPowerPoint();

            PowerPoint.Application app = null;
            PowerPoint.Presentation pres = null;
            try
            {
                app = new PowerPoint.Application();
                // 1. Silent Mode: Do NOT set Visible property (causes PropertySet crash)
                // Use WithWindow parameter below.
                app.DisplayAlerts = PpAlertLevel.ppAlertsNone;

                Log("Opening Presentation...");
                // 2. Open Without Window: Use WithWindow: msoFalse
                pres = app.Presentations.Open(inputFile, NetOffice.OfficeApi.Enums.MsoTriState.msoFalse, NetOffice.OfficeApi.Enums.MsoTriState.msoFalse, NetOffice.OfficeApi.Enums.MsoTriState.msoFalse);

                string absOutputPath = Path.GetFullPath(outputFile);
                Log($"Exporting to: {absOutputPath}");

                try
                {
                    PpFixedFormatIntent intent = PpFixedFormatIntent.ppFixedFormatIntentPrint;
                    if (qualityLevel == 0) intent = PpFixedFormatIntent.ppFixedFormatIntentScreen;

                    // 3. Legacy Format Handling: ExportAsFixedFormat works for both .pptx and .ppt
                    pres.ExportAsFixedFormat(
                        absOutputPath, 
                        PpFixedFormatType.ppFixedFormatTypePDF, 
                        intent, 
                        NetOffice.OfficeApi.Enums.MsoTriState.msoFalse, 
                        PpPrintHandoutOrder.ppPrintHandoutVerticalFirst, 
                        PpPrintOutputType.ppPrintOutputSlides, 
                        NetOffice.OfficeApi.Enums.MsoTriState.msoFalse, 
                        null, 
                        PpPrintRangeType.ppPrintAll, 
                        "", 
                        true, 
                        true, 
                        true, 
                        true, 
                        false
                    );
                }
                catch (Exception exportEx)
                {
                    Log($"Standard Export failed ({exportEx.Message}). Trying SaveAs fallback...");
                    pres.SaveAs(absOutputPath, PpSaveAsFileType.ppSaveAsPDF);
                }

                pres.Close();
                pres = null;
            }
            catch (Exception ex)
            {
                // 4. Safety Wrap Logging
                Log($"PPT->PDF Failed (File: {inputFile}): {ex.Message}");
                throw new Exception($"Cortex Engine Error (PPT->PDF): {ex.Message}");
            }
            finally
            {
                CleanupPowerPoint(app, pres);
            }
        }

        private static void KillGhostPowerPoint()
        {
            try
            {
                System.Diagnostics.Process[] procs = System.Diagnostics.Process.GetProcessesByName("POWERPNT");
                foreach (var p in procs)
                {
                    if (string.IsNullOrEmpty(p.MainWindowTitle))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
            }
            catch { }
        }

        // Cleanup Helpers using Marshal.ReleaseComObject and PID Kill
        private static void CleanupWord(Word.Application app, Word.Document doc)
        {
            int pid = 0;
            if (app != null) pid = GetProcessId(app);

            if (doc != null)
            {
                // Direct File Closing check
                try { doc.Close(false); } catch { }
                try { Marshal.ReleaseComObject(doc); } catch { }
                doc = null;
            }
            if (app != null)
            {
                try { app.Quit(false); Marshal.ReleaseComObject(app); } catch { }
                app = null;
            }
            
            // Immediate File Unlock: Double GC
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // Aggressive Cleanup: Kill if still running
            if (pid > 0)
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        Log($"Aggressive Cleanup: Killed Word process {pid}");
                    }
                }
                catch { }
            }
        }

        private static void CleanupExcel(Excel.Application app, Excel.Workbook wb)
        {
            int pid = 0;
            if (app != null) pid = GetProcessId(app);

            if (wb != null)
            {
                // Direct File Closing check
                try { wb.Close(false); } catch { }
                try { Marshal.ReleaseComObject(wb); } catch { }
                wb = null;
            }
            if (app != null)
            {
                try { app.Quit(); Marshal.ReleaseComObject(app); } catch { }
                app = null;
            }
            
            // Immediate File Unlock: Double GC
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (pid > 0)
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        Log($"Aggressive Cleanup: Killed Excel process {pid}");
                    }
                }
                catch { }
            }
        }

        private static void CleanupPowerPoint(PowerPoint.Application app, PowerPoint.Presentation pres)
        {
            int pid = 0;
            try 
            {
                 if (app != null)
                 {
                     dynamic dApp = app;
                     int hwnd = dApp.HWND; 
                     GetWindowThreadProcessId((IntPtr)hwnd, out uint p);
                     pid = (int)p;
                 }
            }
            catch { }

            // 1. Forceful Exit: Close Presentation FIRST
            if (pres != null)
            {
                try { pres.Close(); } catch { }
                try { Marshal.FinalReleaseComObject(pres); } catch { }
                pres = null;
            }

            // 2. Forceful Exit: Quit App SECOND
            if (app != null)
            {
                try { app.Quit(); } catch { }
                try { Marshal.FinalReleaseComObject(app); } catch { }
                app = null;
            }
            
            // 3. Reference Cleanup: Immediate GC
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // 4. The "dotnet run" Guard: Kill PID if still alive
            if (pid > 0)
            {
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        // Give it a tiny moment to die naturally
                        if (!proc.WaitForExit(2000))
                        {
                            proc.Kill();
                            Log($"Absolute Release: Force killed zombie PowerPoint (PID: {pid})");
                        }
                        proc.Dispose();
                    }
                }
                catch { }
            }
        }
    }
}
