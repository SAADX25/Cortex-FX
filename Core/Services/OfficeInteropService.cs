using System;
using System.Diagnostics;
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
using CortexFX.Core.Interfaces;

namespace CortexFX.Core.Services;

/// <summary>
/// Thread-safe Office COM interop service.
/// Injectable, testable Office conversion service with:
///   - STA thread enforcement for COM operations
///   - PID tracking via IProcessManager for guaranteed cleanup
///   - Per-operation semaphore to prevent "Server Busy" / RPC_E_CALL_REJECTED
///   - Aggressive cleanup with double-GC and PID kill fallback
/// </summary>
public sealed class OfficeInteropService : IOfficeInteropService
{
    private readonly IProcessManager _processManager;
    private readonly string _logPath;

    // Serialize COM operations to prevent "Server Busy" errors.
    // COM automation objects are not thread-safe; concurrent access to the same
    // Office app type (e.g., two Word instances) can corrupt the message pump.
    private readonly SemaphoreSlim _wordLock = new(1, 1);
    private readonly SemaphoreSlim _excelLock = new(1, 1);
    private readonly SemaphoreSlim _powerPointLock = new(1, 1);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public OfficeInteropService(IProcessManager processManager)
    {
        _processManager = processManager;
        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cortex_log.txt");
    }

    // ------------------------------------------------------------------
    // IOfficeInteropService
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public bool IsOfficeInstalled()
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

    /// <inheritdoc />
    public async Task ConvertToPdfAsync(string inputFile, string outputFile, int qualityLevel = 1,
                                         CancellationToken ct = default, IProgress<double>? progress = null)
    {
        string extension = Path.GetExtension(inputFile).ToLowerInvariant();

        switch (extension)
        {
            case ".docx":
            case ".doc":
                await RunOnStaAsync(() => ConvertWordToPdf(inputFile, outputFile, qualityLevel, ct, progress), _wordLock, ct);
                break;
            case ".xlsx":
            case ".xls":
                await RunOnStaAsync(() => ConvertExcelToPdf(inputFile, outputFile, qualityLevel, ct, progress), _excelLock, ct);
                break;
            case ".pptx":
            case ".ppt":
                await RunOnStaAsync(() => ConvertPptToPdf(inputFile, outputFile, qualityLevel, ct, progress), _powerPointLock, ct);
                break;
            default:
                throw new NotSupportedException($"Format '{extension}' is not supported for PDF conversion.");
        }
    }

    /// <inheritdoc />
    public async Task ConvertPdfToWordAsync(string inputFile, string outputFile,
                                             CancellationToken ct = default, IProgress<double>? progress = null)
    {
        await RunOnStaAsync(() =>
        {
            Log($"Starting PDF to Word: {inputFile}");
            if (!IsOfficeInstalled()) throw new InvalidOperationException("Microsoft Office is required.");

            Word.Application? app = null;
            Word.Document? doc = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(10);

                app = new Word.Application { Visible = false };
                app.DisplayAlerts = WdAlertLevel.wdAlertsNone;
                TrackOfficePid(app);

                progress?.Report(30);
                doc = app.Documents.Open(inputFile, false, true);

                ct.ThrowIfCancellationRequested();
                progress?.Report(60);

                doc.SaveAs2(outputFile, WdSaveFormat.wdFormatXMLDocument);
                doc.Close(false);
                doc = null;

                Log("PDF→Word conversion successful.");
                progress?.Report(100);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"PDF→Word Error: {ex.Message}");
                throw new InvalidOperationException($"Cortex Engine Error (PDF→Word): {ex.Message}", ex);
            }
            finally
            {
                CleanupWord(app, doc);
            }
        }, _wordLock, ct);
    }

    /// <inheritdoc />
    public async Task ConvertWordToPowerPointAsync(string inputFile, string outputFile,
                                                    CancellationToken ct = default, IProgress<double>? progress = null)
    {
        // This acquires both Word and PowerPoint locks since it uses both COM servers
        await _wordLock.WaitAsync(ct);
        try
        {
            await _powerPointLock.WaitAsync(ct);
            try
            {
                await RunOnStaThreadAsync(() =>
                    ConvertWordToPptCore(inputFile, outputFile, ct, progress));
            }
            finally { _powerPointLock.Release(); }
        }
        finally { _wordLock.Release(); }
    }

    /// <inheritdoc />
    public async Task ConvertPdfToPowerPointAsync(string inputFile, string outputFile,
                                                   CancellationToken ct = default, IProgress<double>? progress = null)
    {
        string tempDocx = Path.Combine(
            Path.GetDirectoryName(outputFile) ?? Path.GetTempPath(),
            Guid.NewGuid() + ".docx");

        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(10);

            // Stage 1: PDF → Word (0-50%)
            var wordProgress = new Progress<double>(p => progress?.Report(10 + (p * 0.4)));
            await ConvertPdfToWordAsync(inputFile, tempDocx, ct, wordProgress);

            progress?.Report(50);

            // Wait for file system to release the handle
            await WaitForFileAsync(tempDocx, ct);

            ct.ThrowIfCancellationRequested();
            progress?.Report(60);

            // Stage 2: Word → PowerPoint (60-100%)
            var pptProgress = new Progress<double>(p => progress?.Report(60 + (p * 0.4)));
            await ConvertWordToPowerPointAsync(tempDocx, outputFile, ct, pptProgress);

            progress?.Report(100);
        }
        finally
        {
            try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // Core conversion methods (run on STA thread)
    // ------------------------------------------------------------------

    private void ConvertWordToPdf(string inputFile, string outputFile, int qualityLevel,
                                   CancellationToken ct, IProgress<double>? progress)
    {
        Log($"Starting Word→PDF: {inputFile}");
        Word.Application? app = null;
        Word.Document? doc = null;
        try
        {
            progress?.Report(10);
            app = new Word.Application { Visible = false };
            app.DisplayAlerts = WdAlertLevel.wdAlertsNone;
            TrackOfficePid(app);

            doc = app.Documents.Open(inputFile, false, true);

            var optimization = qualityLevel == 0
                ? WdExportOptimizeFor.wdExportOptimizeForOnScreen
                : WdExportOptimizeFor.wdExportOptimizeForPrint;

            ct.ThrowIfCancellationRequested();
            progress?.Report(50);

            string absPath = Path.GetFullPath(outputFile);
            doc.ExportAsFixedFormat(absPath, WdExportFormat.wdExportFormatPDF, false, optimization);
            doc.Close(false);
            doc = null;

            Log("Word→PDF successful.");
            progress?.Report(100);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Word→PDF Error: {ex.Message}");
            throw new InvalidOperationException($"Cortex Engine Error (Word→PDF): {ex.Message}", ex);
        }
        finally
        {
            CleanupWord(app, doc);
        }
    }

    private void ConvertExcelToPdf(string inputFile, string outputFile, int qualityLevel,
                                    CancellationToken ct, IProgress<double>? progress)
    {
        Log($"Starting Excel→PDF: {inputFile}");
        Excel.Application? app = null;
        Excel.Workbook? wb = null;
        try
        {
            progress?.Report(10);
            app = new Excel.Application { Visible = false };
            app.DisplayAlerts = false;
            TrackOfficePid(app);

            wb = app.Workbooks.Open(inputFile, 0, true);

            var quality = qualityLevel == 0
                ? XlFixedFormatQuality.xlQualityMinimum
                : XlFixedFormatQuality.xlQualityStandard;

            ct.ThrowIfCancellationRequested();
            progress?.Report(50);

            string absPath = Path.GetFullPath(outputFile);
            wb.ExportAsFixedFormat(XlFixedFormatType.xlTypePDF, absPath, quality, true, false);
            wb.Close(false);
            wb = null;

            Log("Excel→PDF successful.");
            progress?.Report(100);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Excel→PDF Error: {ex.Message}");
            throw new InvalidOperationException($"Cortex Engine Error (Excel→PDF): {ex.Message}", ex);
        }
        finally
        {
            CleanupExcel(app, wb);
        }
    }

    private void ConvertPptToPdf(string inputFile, string outputFile, int qualityLevel,
                                  CancellationToken ct, IProgress<double>? progress)
    {
        Log($"Starting PPT→PDF: {inputFile}");

        // Kill ghost PowerPoint processes before creating a new instance
        _processManager.KillZombieProcesses("POWERPNT");

        PowerPoint.Application? app = null;
        PowerPoint.Presentation? pres = null;
        try
        {
            progress?.Report(10);
            app = new PowerPoint.Application();
            // NOTE: Do NOT set Visible property on PowerPoint.Application — causes PropertySet crash
            app.DisplayAlerts = PpAlertLevel.ppAlertsNone;
            TrackOfficePid(app);

            progress?.Report(30);
            pres = app.Presentations.Open(inputFile,
                NetOffice.OfficeApi.Enums.MsoTriState.msoFalse,
                NetOffice.OfficeApi.Enums.MsoTriState.msoFalse,
                NetOffice.OfficeApi.Enums.MsoTriState.msoFalse);

            ct.ThrowIfCancellationRequested();
            progress?.Report(60);

            string absPath = Path.GetFullPath(outputFile);
            var intent = qualityLevel == 0
                ? PpFixedFormatIntent.ppFixedFormatIntentScreen
                : PpFixedFormatIntent.ppFixedFormatIntentPrint;

            try
            {
                pres.ExportAsFixedFormat(
                    absPath, PpFixedFormatType.ppFixedFormatTypePDF, intent,
                    NetOffice.OfficeApi.Enums.MsoTriState.msoFalse,
                    PpPrintHandoutOrder.ppPrintHandoutVerticalFirst,
                    PpPrintOutputType.ppPrintOutputSlides,
                    NetOffice.OfficeApi.Enums.MsoTriState.msoFalse,
                    null, PpPrintRangeType.ppPrintAll, "", true, true, true, true, false);
            }
            catch
            {
                // Fallback: some PPT versions don't support all ExportAsFixedFormat params
                pres.SaveAs(absPath, PpSaveAsFileType.ppSaveAsPDF);
            }

            pres.Close();
            pres = null;

            Log("PPT→PDF successful.");
            progress?.Report(100);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"PPT→PDF Error: {ex.Message}");
            throw new InvalidOperationException($"Cortex Engine Error (PPT→PDF): {ex.Message}", ex);
        }
        finally
        {
            CleanupPowerPoint(app, pres);
        }
    }

    private void ConvertWordToPptCore(string inputFile, string outputFile,
                                       CancellationToken ct, IProgress<double>? progress)
    {
        Log($"Starting Word→PPT: {inputFile}");
        PowerPoint.Application? pptApp = null;
        Word.Application? wordApp = null;
        Word.Document? sourceDoc = null;
        PowerPoint.Presentation? pres = null;

        try
        {
            progress?.Report(10);

            pptApp = new PowerPoint.Application();
            pptApp.DisplayAlerts = PpAlertLevel.ppAlertsNone;
            TrackOfficePid(pptApp);

            pres = pptApp.Presentations.Add(NetOffice.OfficeApi.Enums.MsoTriState.msoFalse);

            wordApp = new Word.Application { Visible = false };
            wordApp.DisplayAlerts = WdAlertLevel.wdAlertsNone;
            TrackOfficePid(wordApp);

            sourceDoc = wordApp.Documents.Open(inputFile, false, true);

            int paragraphCount = sourceDoc.Paragraphs.Count;
            int currentPara = 1;
            const int PARAS_PER_SLIDE = 7;

            progress?.Report(30);

            while (currentPara <= paragraphCount)
            {
                ct.ThrowIfCancellationRequested();

                double percent = 30 + (60.0 * currentPara / paragraphCount);
                progress?.Report(percent);

                var slide = pres.Slides.Add(pres.Slides.Count + 1, PpSlideLayout.ppLayoutText);
                slide.Shapes[1].TextFrame.TextRange.Text = "Document Content";

                string slideText = "";
                for (int i = 0; i < PARAS_PER_SLIDE && currentPara <= paragraphCount; i++)
                {
                    string text = sourceDoc.Paragraphs[currentPara].Range.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        slideText += text + "\r\n";
                    }
                    currentPara++;
                }

                var bodyShape = slide.Shapes[2];
                bodyShape.TextFrame.TextRange.Text = slideText;
                bodyShape.TextFrame.TextRange.Font.Name = "Calibri";
                bodyShape.TextFrame.TextRange.Font.Size = 20;
                bodyShape.TextFrame.TextRange.ParagraphFormat.Alignment = PpParagraphAlignment.ppAlignLeft;
            }

            sourceDoc.Close(false);
            sourceDoc = null;

            if (!ct.IsCancellationRequested)
            {
                progress?.Report(95);
                string absPath = Path.GetFullPath(outputFile);
                pres.SaveAs(absPath, PpSaveAsFileType.ppSaveAsOpenXMLPresentation);
            }
            pres.Close();
            pres = null;

            Log("Word→PPT successful.");
            progress?.Report(100);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Word→PPT Error: {ex.Message}");
            throw new InvalidOperationException($"Cortex Engine Error (Word→PPT): {ex.Message}", ex);
        }
        finally
        {
            CleanupWord(wordApp, sourceDoc);
            CleanupPowerPoint(pptApp, pres);
        }
    }

    // ------------------------------------------------------------------
    // STA Thread Infrastructure
    // ------------------------------------------------------------------

    /// <summary>
    /// Run an action on a dedicated STA thread with semaphore serialization.
    /// COM objects MUST be created and used on an STA thread.
    /// </summary>
    private async Task RunOnStaAsync(System.Action action, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            await RunOnStaThreadAsync(action);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Execute an action on a new STA thread. Returns a Task that completes
    /// when the action finishes.
    /// </summary>
    private static Task RunOnStaThreadAsync(System.Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    // ------------------------------------------------------------------
    // PID tracking helpers
    // ------------------------------------------------------------------

    private void TrackOfficePid(object application)
    {
        int pid = GetOfficePid(application);
        if (pid > 0)
        {
            _processManager.TrackProcess(pid);
        }
    }

    private static int GetOfficePid(object application)
    {
        try
        {
            dynamic app = application;
            int hwnd = app.Hwnd;
            GetWindowThreadProcessId((IntPtr)hwnd, out uint pid);
            return (int)pid;
        }
        catch
        {
            // PowerPoint uses HWND (all-caps) property
            try
            {
                dynamic app = application;
                int hwnd = app.HWND;
                GetWindowThreadProcessId((IntPtr)hwnd, out uint pid);
                return (int)pid;
            }
            catch { return 0; }
        }
    }

    // ------------------------------------------------------------------
    // Cleanup: COM release + GC + PID kill fallback
    // ------------------------------------------------------------------

    private void CleanupWord(Word.Application? app, Word.Document? doc)
    {
        int pid = app != null ? GetOfficePid(app) : 0;

        if (doc != null) { try { doc.Close(false); } catch { } try { Marshal.ReleaseComObject(doc); } catch { } }
        if (app != null) { try { app.Quit(false); Marshal.ReleaseComObject(app); } catch { } }

        ForceGC();
        KillIfAlive(pid);
    }

    private void CleanupExcel(Excel.Application? app, Excel.Workbook? wb)
    {
        int pid = app != null ? GetOfficePid(app) : 0;

        if (wb != null) { try { wb.Close(false); } catch { } try { Marshal.ReleaseComObject(wb); } catch { } }
        if (app != null) { try { app.Quit(); Marshal.ReleaseComObject(app); } catch { } }

        ForceGC();
        KillIfAlive(pid);
    }

    private void CleanupPowerPoint(PowerPoint.Application? app, PowerPoint.Presentation? pres)
    {
        int pid = app != null ? GetOfficePid(app) : 0;

        if (pres != null) { try { pres.Close(); } catch { } try { Marshal.FinalReleaseComObject(pres); } catch { } }
        if (app != null) { try { app.Quit(); } catch { } try { Marshal.FinalReleaseComObject(app); } catch { } }

        ForceGC();

        // PowerPoint is the worst offender — give it 2s to die naturally, then kill
        if (pid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited && !proc.WaitForExit(2000))
                {
                    proc.Kill();
                    Log($"Force killed zombie PowerPoint (PID: {pid})");
                }
                proc.Dispose();
            }
            catch { }
        }
    }

    private static void ForceGC()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private void KillIfAlive(int pid)
    {
        if (pid <= 0) return;
        try
        {
            var proc = Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                proc.Kill();
                Log($"Aggressive cleanup: Killed Office process (PID: {pid})");
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Utilities
    // ------------------------------------------------------------------

    private static async Task WaitForFileAsync(string path, CancellationToken ct, int maxRetries = 20)
    {
        int retries = 0;
        while (!File.Exists(path) && retries < maxRetries)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct);
            retries++;
        }
        // Extra wait for file handle release
        await Task.Delay(1000, ct);
    }

    private void Log(string message)
    {
        if (message.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleLogger.Error("Office", message);
        }
        else if (message.Contains("successful", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleLogger.Success("Office", message);
        }
        else
        {
            ConsoleLogger.Info("Office", message);
        }

        try
        {
            File.AppendAllText(_logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}");
        }
        catch { }
    }
}
