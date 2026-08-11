# Cortex FX Architecture

Human-friendly map of the project so you can add features without hunting through a flat root folder.

## Folder map

```text
CortexFX/
  App.xaml(.cs)              Entry point + DI registration only
  MainWindow.xaml(.cs)       Shell: dashboard, navigation, universal convert, audio cutter
  Assets/                    App icons (logo.ico)
  Controls/                  Reusable UI pieces (ToolCard, …)
  Converters/                WPF value converters
  Dialogs/                   Modal windows (ModernSuccessDialog, ModernConfirmDialog)
  Views/
    Video/                   Feature UIs: VideoCompressorView, VideoCutterView
  ViewModels/                MVVM helpers (optional for new tools)
  Models/                    ConversionJob, FileModel, …
  Core/
    Audio/                   Playback helpers (LiveSpectrumAnalyzer)
    Configuration/           IAppConfiguration
    Constants/               MediaTypes (formats + conversion rules)
    Interfaces/              Service contracts + ProcessExecutionException
    Services/
      Media/                 FFmpegService, MagickService
      Documents/             ConversionRouter, Office, PdfRender, OptionalConversion
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
| Audio Cutter | Trim UI, waveform, export |
| Audio Choice Overlay | Trim vs convert prompt |

**Next cleanup (optional):** extract Audio Cutter into `Views/Audio/AudioCutterView` the same way Video tools were extracted.

## DI registration

All new services go in `App.ConfigureServices` inside `App.xaml.cs`:

- Singletons for engines / routers / config
- Transient for ViewModels / windows that need a fresh instance

## Preferences & logs

- User prefs (e.g. audio volume): `%LocalAppData%\CortexFX\userprefs.json` via `UserPreferences`
- Logs: `ConsoleLogger` under the same LocalAppData tree
