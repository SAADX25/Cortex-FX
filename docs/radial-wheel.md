# Native radial wheel

The conversion wheel runs inside Cortex FX's existing process, single-instance lifecycle and tray. It uses WPF's native OLE `FileDrop` target and a 25 ms Win32 key/mouse poll. It does not read the clipboard to discover dragged files and does not install global mouse hooks.

| Default gesture | Actions |
| --- | --- |
| Shift + drag | Compatible conversion formats; every original format is excluded from a batch |
| Alt + drag | Image compression, metadata removal, 50% resizing; video compression, MP3 extraction, animated GIF; PDF compression, splitting, PNG page rendering, PDF merging when several PDFs are selected |
| Ctrl + drag | One ZIP, full paths copied to the clipboard, SHA-256 manifest saved and copied |

The settings overlay contains a **Radial wheel settings** button. The existing tray menu includes **Open conversion wheel…**, **Radial wheel settings…**, **Enable drag wheel** and **Cancel wheel conversion**. The file picker provides an alternative to global drag gestures: choose files, then click a segment or select it with Left/Right/Tab and Enter. Esc dismisses an idle wheel or cancels its job.

Preferences are separate from existing Audio Cutter settings: `%LOCALAPPDATA%\CortexFX\radial-wheel.json`. Quality, PDF DPI, notifications, enable state and the three distinct modifier assignments can be customized. A blank FFmpeg override uses the existing `Resources/ffmpeg.exe`; a missing path disables media segments with an explanation.

## Engines and output

- Images: the existing Magick.NET dependency; the wheel supports JPG, PNG, WEBP, HEIC, AVIF, BMP, TIFF, ICO and PDF. JPEG/BMP export flattens transparency on white. Several images dropped onto PDF create one PDF.
- Media: the existing FFmpeg service, tracked by the existing process manager, including hardware fallback. The wheel opts into indeterminate progress when duration is unavailable instead of inventing a percentage. Video-to-audio extraction omits video streams; audio conversion retains its existing cover-art behavior.
- PDF operations: PDFsharp 6.2.4 for merge, split and lossless compression; the existing Pdfium renderer for page images. The explicit `System.Security.Cryptography.Pkcs` 10.0.13 reference keeps PDFsharp's transitive dependency on a patched version.
- Other document, archive and e-book pairs reuse `IConversionRouter` and its existing local engine requirements, including Microsoft Office, OCR or optional 7-Zip/Calibre where appropriate. This integration does not replace those conversion engines or change their document-layout limitations.
- ZIP and SHA-256 use .NET's local libraries. SHA-256 includes full paths so identically named files in different directories remain distinguishable.

Each wheel job stages output in a uniquely named sibling folder. It publishes completed files with an exclusive name reservation, or publishes a complete page directory by renaming it. Existing output names get a numeric suffix; originals are never overwritten. An error/cancellation removes the current staging folder and reports earlier saved outputs in a partially completed batch. ZIP bundles, merged images/PDFs and manifests are saved beside the first selected file. Up to 100 regular files can be selected. A clipboard lock is reported without discarding saved files.

Image/PDF compression keeps the original bytes in the new output when optimization would increase size. Complex document conversions retain the existing Cortex FX engine behavior. PDF files must be readable without an unknown password. EXIF stripping and image format conversion process a still image/frame; animated video-to-GIF uses FFmpeg.

## Code layout

`Core/Radial` contains settings, the pure gesture state machine, Win32 monitoring, routing, safe output publication and job execution. `Views/Radial` contains the wheel renderer, native settings window and a locally scoped dark/crimson resource dictionary. `MainWindow.Radial.cs` connects the feature to the existing shell. DI registrations live in `App.xaml.cs`.

The integration preserves the existing main-window startup, close-to-tray behavior and other tool settings. The wheel runs after the app starts, including while the main window is hidden. Quit from the existing tray menu stops monitoring and cancels the active wheel job; force-closing the process may leave a `.cortex-wheel-*` staging directory, while original files remain intact.

## Verification

```powershell
dotnet test CortexFX.slnx -c Release
```

Tests include routing, alias exclusion, batch intersections, gesture thresholds/latching, ordinary-click release, polar hit testing, output collisions/concurrency, staging cleanup, settings validation, native image conversion (including HEIC/AVIF), real PDF merge/split/render/compression, ZIP, hashes, cancellation, partial failures and WPF rendering.

For the optional actual FFmpeg integration test:

```powershell
$env:CORTEXFX_TEST_FFMPEG = "$PWD\Resources\ffmpeg.exe"
$env:CORTEXFX_RADIAL_ARTIFACTS = "$PWD\docs\validation\radial"
dotnet test CortexFX.slnx -c Release --logger "trx;LogFileName=radial-integration.trx"
```

It encodes all six audio outputs and all six video/GIF outputs, then checks extraction and compression. Without FFmpeg, only that integration test is explicitly skipped. Setting `CORTEXFX_RADIAL_ARTIFACTS` saves real WPF screenshots. The pre-integration project had 53 passing tests; the integrated suite has 84 passing tests with FFmpeg enabled.

Manual Windows acceptance remains necessary for actual Explorer/desktop drag gestures and mixed-DPI monitor placement: run Cortex FX normally (at the same privilege level as Explorer), close the main window to the tray, drag a real PNG with Shift and drop onto WEBP, try Alt with two PDFs and choose merge, then try Ctrl with several files. Check that originals remain, duplicate outputs receive new names, and the wheel appears correctly on each monitor. Automated tests exercise rendering and gesture state; they do not simulate a human Explorer drag.
