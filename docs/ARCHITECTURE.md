# Cortex FX Architecture

Human-friendly map of the project so you can add features without hunting through a flat root folder.

## Folder map

```text
CortexFX/
  App.xaml(.cs)              Entry point + DI registration only
  MainWindow.xaml(.cs)       Shell: dashboard, navigation, universal convert
  MainWindow.AudioCutter.cs  Audio cutter state and playback logic (partial shell)
  Assets/                    App icons (logo.ico)
  Controls/                  Reusable UI pieces (ToolCard, …)
  Converters/                WPF value converters
  Dialogs/                   Modal windows (ModernSuccessDialog, ModernConfirmDialog)
  Views/
    Video/                   Feature UIs: VideoCompressorView, VideoCutterView
    Audio/                   AudioCutterView markup and event forwarding
  ViewModels/                MVVM helpers (optional for new tools)
  Models/                    ConversionJob, FileModel, …
  Core/
    Audio/                   Playback helpers (LiveSpectrumAnalyzer)
    Configuration/           IAppConfiguration
    Constants/               MediaTypes (formats + conversion rules)
    Interfaces/              Service contracts + ProcessExecutionException
    Services/
      Media/                 FFmpegService, MagickService
      Documents/             ConversionRouter, Office, PdfRender, PDF analysis/OCR/Word, optional conversion
      Infrastructure/        ProcessManager, logging, prefs, registry, validation
  Resources/                 Bundled tools (ffmpeg, magick, …) — do not put C# here
  docs/                      This guide
```

## How conversion flows

```text
UI (MainWindow / Views)
    → IConversionRouter (Documents/ConversionRouter)
        → Media (FFmpeg / Magick)
        → Documents (Office / Pdfium / optional engines)
    → Resources/*.exe on disk
```

Specialized tools (Video Compressor, Video Cutter, Audio Cutter) may call engines directly for a tighter UX, but shared format rules still live in `MediaTypes`.

PDF to DOCX uses `IPdfToWordService`: Pdfium checks each page for extractable text; a text PDF goes to serialized Word PDF Reflow, while a scanned PDF goes to optional Tesseract OCR after 300 DPI rendering. Word failure or a valid Word DOCX with no editable text triggers OCR fallback. Package validity and editable text are checked separately; a malformed package is reported as a failure. OCR uses `eng+ara` trained data and is optional; an absent executable gives a user-facing error. Intermediate files are deleted. PDF to PPTX uses this same DOCX stage.

Batch work is limited globally to eight jobs and by engine: Office 1, FFmpeg 2, ImageMagick 3, PDF rendering 2, PDF to Word/OCR 1. Office automation runs on STA threads. Process cleanup tracks PID and start time; it never sweeps all Office processes by name. FFmpeg keeps bounded recent diagnostic lines and discards repetitive progress output.

## How to add a new tool (checklist)

Example: **Image Cropper**

1. **Dashboard card** — add a `ToolCard` in `MainWindow.xaml` with a unique tag (e.g. `IMAGE_CROPPER`).
2. **Navigation** — wire the tag in `MainWindow.xaml.cs` (`GetModeForToolTag` / `SwitchToMode` / `ConfigureTool`).
3. **View** — create `Views/Image/ImageCropperView.xaml` + `.xaml.cs` under namespace `CortexFX.Views.Image`.
4. **Host the view** — add the control to `MainWindow.xaml` next to the other feature views and show/hide it like Video tools.
5. **Engine logic** — put FFmpeg/Magick/process code in `Core/Services/Media/` (or Documents if Office/PDF). Prefer an interface in `Core/Interfaces/` and register it in `App.xaml.cs`.
6. **Formats** — update `Core/Constants/MediaTypes.cs` if the tool needs new extensions or conversion rules.
7. **Dialogs** — reuse `Dialogs/ModernSuccessDialog` / `ModernConfirmDialog` instead of Windows `MessageBox` when possible.
8. **Build** — `dotnet build .\CortexFX.csproj -c Debug` and smoke-test the happy path.

Keep Views thin: UI + calling services. Keep Services free of Window/UserControl types.

## Where MainWindow regions live

`MainWindow.xaml.cs` is still the shell. Use Visual Studio / Cursor region folding:

| Region | Purpose |
|--------|---------|
| Fields | State |
| Construction & Startup | DI ctor, FFME, resource checks |
| Dashboard / Navigation | Modes, tool cards, back |
| Files & Formats | File list helpers, format filters |
| Settings / Overlays / Shell | Settings, folders, context menu |
| Drag-Drop & File Picking | Drop zone, open dialogs |
| Universal Conversion | Batch convert pipeline |
| Audio Cutter | In `MainWindow.AudioCutter.cs`; view markup in `Views/Audio` |
| Audio Choice Overlay | Trim vs convert prompt |

Audio playback and shell navigation still share `MainWindow` state; the view extraction preserves the existing controls and behavior. A later pass can move the playback controller out of the partial window class.

## Tests and CI

`CortexFX.Tests` contains pure unit tests with fake engines, so Microsoft Office and bundled binaries are not required for the test run. Use `dotnet restore CortexFX.slnx`, `dotnet build CortexFX.slnx -c Release`, and `dotnet test CortexFX.slnx -c Release`. `.github/workflows/ci.yml` runs these steps on a Windows runner with .NET 10.

## DI registration

All new services go in `App.ConfigureServices` inside `App.xaml.cs`:

- Singletons for engines / routers / config
- Transient for ViewModels / windows that need a fresh instance

## Preferences & logs

- User prefs (e.g. audio volume): `%LocalAppData%\CortexFX\userprefs.json` via `UserPreferences`
- Logs: `ConsoleLogger` under the same LocalAppData tree
