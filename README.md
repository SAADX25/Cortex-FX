<div align="center">

<img src="Assets/logo.ico" width="80" alt="Cortex FX Logo"/>

# Cortex FX

**A powerful local file conversion desktop app for Windows.**  
Convert images, videos, audio, documents, archives, and e-books — entirely offline, on your machine.

[![Version](https://img.shields.io/badge/version-1.6.0-blue?style=for-the-badge)](https://github.com/SAADX25/Cortex-FX/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078d4?style=for-the-badge&logo=windows)](https://github.com/SAADX25/Cortex-FX)
[![Framework](https://img.shields.io/badge/.NET-10.0--windows-512bd4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-WPF-68217a?style=for-the-badge&logo=microsoft)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![License](https://img.shields.io/badge/license-TBD-lightgrey?style=for-the-badge)](LICENSE)

</div>

---

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Project Structure](#project-structure)
- [Architecture](#architecture)
- [Required External Tools](#required-external-tools)
- [Quick Start](#quick-start)
- [Build From Source](#build-from-source)
- [Publish & Release](#publish--release)
- [Troubleshooting](#troubleshooting)
- [Known Limitations](#known-limitations)
- [License](#license)

---

## Overview

**Cortex FX** is a Windows WPF desktop application that handles file conversion locally — no cloud, no uploads, no internet required. It routes conversion work through trusted local engines:

| Engine | Role |
|---|---|
| **FFmpeg** | Video & audio conversion, trimming, compression |
| **ImageMagick** | Image format conversion (including HEIC/HEIF) |
| **Microsoft Office COM** | Document conversion (DOCX, PPTX, XLSX -> PDF, etc.) |
| **Poppler / pdftocairo** | PDF rendering and PDF -> image export |
| **LibreOffice** *(optional)* | Open-source document conversion fallback |
| **7-Zip** *(optional)* | Archive extraction and compression |
| **Calibre** *(optional)* | E-book format conversion |

---

## Features

### Images
- Convert between **JPG, PNG, WEBP, BMP, ICO, TIFF, GIF, HEIC/HEIF**
- Powered by the bundled ImageMagick build

### Video & Audio
- **Video:** MP4, MKV, MOV, AVI, WEBM
- **Audio:** MP3, WAV, AAC, FLAC, OGG, M4A
- Built-in **Video Compressor** and **Video Cutter** tools
- Integrated **Audio Cutter** with live waveform and spectrum analysis

### Documents
- **PDF** <-> DOCX, PPTX, XLSX, ODT, RTF, TXT
- PDF to image and image merge to PDF
- Office COM automation for faithful document rendering

### Archives *(optional — requires 7-Zip)*
- Input: ZIP, RAR | Output: 7Z, ZIP, TAR

### E-Books *(optional — requires Calibre)*
- Convert between EPUB, MOBI, AZW3, PDF

### App Highlights
- Fully offline — no telemetry, no cloud
- Batch conversion support
- Local logging under `%LOCALAPPDATA%\Cortex FX\Logs`
- Settings panel with **Resource Status** checker
- Modern dark UI with smooth animations

---

## Project Structure

```
Cortex-FX/
|
|-- Assets/                         # App branding & icons
|   `-- logo.ico                    # Application icon
|
|-- Controls/                       # Reusable WPF controls
|   |-- ToolCard.xaml               # Dashboard tool card UI
|   `-- ToolCard.xaml.cs            # Tool card code-behind
|
|-- Converters/                     # WPF value converters
|   `-- StringToBrushConverter.cs   # String to Brush binding helper
|
|-- Core/                           # Business logic layer (no UI types)
|   |-- Audio/
|   |   `-- LiveSpectrumAnalyzer.cs # Real-time audio spectrum analysis
|   |
|   |-- Configuration/
|   |   `-- IAppConfiguration.cs   # App settings contract
|   |
|   |-- Constants/
|   |   `-- MediaTypes.cs          # Format definitions & conversion rules
|   |
|   |-- Interfaces/                 # Service contracts
|   |   |-- IConversionRouter.cs    # Main conversion dispatch interface
|   |   |-- IFFmpegService.cs       # FFmpeg operations interface
|   |   |-- IMagickService.cs       # ImageMagick operations interface
|   |   |-- IOfficeInteropService.cs# Office COM automation interface
|   |   |-- IOptionalConversionService.cs # Optional engines interface
|   |   |-- IPdfRenderService.cs    # PDF rendering interface
|   |   |-- IProcessManager.cs      # External process management interface
|   |   `-- IResourceValidationService.cs # Resource validation interface
|   |
|   `-- Services/
|       |-- Documents/              # Document & PDF conversion services
|       |   |-- ConversionRouter.cs        # Dispatches to correct engine
|       |   |-- OfficeInteropService.cs    # Microsoft Office COM automation
|       |   |-- OptionalConversionService.cs # LibreOffice / Calibre / 7-Zip
|       |   `-- PdfRenderService.cs        # PDF to image via Pdfium
|       |
|       |-- Infrastructure/         # Cross-cutting concerns
|       |   |-- AppConfiguration.cs        # Settings read/write
|       |   |-- ConsoleLogger.cs           # Local file logger
|       |   |-- ProcessManager.cs          # External process execution
|       |   |-- RegistryManager.cs         # Windows registry helpers
|       |   |-- ResourceValidationService.cs # Validates bundled tools
|       |   `-- UserPreferences.cs         # Per-user preference store
|       |
|       `-- Media/                  # Media conversion services
|           |-- FFmpegService.cs           # Video & audio via FFmpeg
|           `-- MagickService.cs           # Image ops via ImageMagick
|
|-- Dialogs/                        # Modal dialog windows
|   |-- ModernConfirmDialog.xaml    # Confirmation prompt UI
|   |-- ModernConfirmDialog.xaml.cs # Confirm dialog code-behind
|   |-- ModernSuccessDialog.xaml    # Success/result dialog UI
|   `-- ModernSuccessDialog.xaml.cs # Success dialog code-behind
|
|-- Models/                         # Data models
|   |-- ConversionJob.cs            # Represents a single conversion task
|   `-- FileModel.cs                # File entry with metadata
|
|-- Resources/                      # Bundled native tools & DLLs
|   |-- ffmpeg.exe                  # Video/audio engine
|   |-- magick.exe                  # Image processing engine
|   |-- pdftocairo.exe              # PDF to image renderer
|   |-- ffmpeg_libs/                # FFmpeg shared libraries
|   |   |-- avcodec-58.dll
|   |   |-- avformat-58.dll
|   |   |-- avutil-56.dll
|   |   |-- swresample-3.dll
|   |   `-- swscale-5.dll
|   `-- [ImageMagick DLLs]          # CORE_RL_*.dll & codec support files
|
|-- ViewModels/                     # MVVM view models
|   |-- AudioEditorViewModel.cs     # Audio cutter state & commands
|   |-- ConversionViewModel.cs      # Universal conversion pipeline state
|   |-- MainViewModel.cs            # Shell navigation state
|   `-- SettingsViewModel.cs        # Settings panel state
|
|-- Views/
|   `-- Video/                      # Standalone feature views
|       |-- VideoCompressorView.xaml    # Video compressor UI
|       |-- VideoCompressorView.xaml.cs # Video compressor logic
|       |-- VideoCutterView.xaml        # Video cutter UI
|       `-- VideoCutterView.xaml.cs     # Video cutter logic
|
|-- docs/
|   `-- ARCHITECTURE.md             # Developer architecture guide
|
|-- App.xaml                        # Application entry point + DI setup
|-- App.xaml.cs                     # Service registration (ConfigureServices)
|-- AssemblyInfo.cs                 # WPF theme resource declarations
|-- MainWindow.xaml                 # Shell: dashboard, navigation, convert, audio cutter
|-- MainWindow.xaml.cs              # Shell code-behind (all regions)
|-- CortexFX.csproj                 # Project file (.NET 10, WPF, NuGet refs)
|-- build.ps1                       # Publish + installer + ZIP build script
|-- setup.iss                       # Inno Setup installer script
|-- Cortex_FX.bat                   # Quick dev launch helper
`-- .gitignore                      # Git ignore rules
```

---

## Architecture

```
UI Layer  (MainWindow / Views / Dialogs)
    |
    v
IConversionRouter  (Core/Services/Documents/ConversionRouter)
    |
    |---> Media Services
    |       FFmpegService   -->  ffmpeg.exe
    |       MagickService   -->  magick.exe
    |
    `---> Document Services
            OfficeInteropService     --> Microsoft Office COM
            PdfRenderService         --> pdftocairo.exe / Pdfium
            OptionalConversionService--> LibreOffice / Calibre / 7-Zip
```

> See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full developer guide, including how to add new conversion tools.

### NuGet Dependencies

| Package | Purpose |
|---|---|
| `CommunityToolkit.Mvvm` | MVVM helpers, relay commands, observable properties |
| `FFME.Windows` | WPF-native FFmpeg media player (audio cutter waveform) |
| `Magick.NET-Q8-AnyCPU` | ImageMagick .NET bindings |
| `Microsoft.Extensions.DependencyInjection` | DI container for service registration |
| `NAudio` | Audio analysis & spectrum visualization |
| `NetOfficeFw.Word/Excel/PowerPoint` | Office COM automation via NetOffice |
| `PdfiumViewer` | PDF rendering and preview |
| `System.Drawing.Common` | GDI+ image helpers |

---

## Required External Tools

The app expects the following layout in the published folder:

```
CortexFX.exe
Resources/
  ffmpeg.exe
  magick.exe
  pdftocairo.exe
  ffmpeg_libs/
    avcodec-58.dll
    avformat-58.dll
    avutil-56.dll
    swresample-3.dll
    swscale-5.dll
```

### Optional Engines

Place these in `Resources/` or their default Program Files install path:

```
Resources/
  LibreOffice/program/soffice.exe   # Open-source document conversion
  7z.exe                            # Archive support
  7za.exe
  Calibre/ebook-convert.exe         # E-book conversion
```

> **Tip:** Open **Settings > Resource Status** in the app to see exact expected paths and which tools are found or missing.

### Resource Resolution Order

1. `{AppContext.BaseDirectory}\Resources` — installed / published builds
2. Project `Resources\` folder — Debug builds running from source

---

## Quick Start

1. Download the latest release from [GitHub Releases](https://github.com/SAADX25/Cortex-FX/releases).
2. Run the installer (`CortexFX_Setup_v1.6.0.exe`) **or** extract the portable ZIP.
3. Ensure the `Resources` folder is present beside `CortexFX.exe` (portable only).
4. Open Cortex FX, pick a tool from the dashboard, add files, choose an output folder, and click **Convert**.

> If Windows SmartScreen warns on first launch, click **More info -> Run anyway** only if the file came from the official release page.

---

## Build From Source

### Prerequisites

- Windows 10 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`net10.0-windows`)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) *(installer only)*
- Microsoft Office *(Office COM conversions only)*

### Restore & Build

```powershell
dotnet restore .\CortexFX.csproj
dotnet build   .\CortexFX.csproj -c Release
```

### Run from Source

```powershell
dotnet run --project .\CortexFX.csproj
```

---

## Publish & Release

### Manual Publish

```powershell
dotnet publish .\CortexFX.csproj -c Release -r win-x64 --self-contained true -o .\Publish\CortexFX_v1.6.0
```

### Using the Build Script

| Command | Description |
|---|---|
| `.\build.ps1` | Publish -> validate resources -> build installer |
| `.\build.ps1 -CreatePortableZip` | Also creates a portable ZIP alongside the installer |
| `.\build.ps1 -RemoveStagingFolder` | Deletes `Publish\CortexFX_vX.Y.Z` after the installer is built |
| `.\build.ps1 -SkipInstaller -CreatePortableZip` | Portable ZIP only, no installer |

The script: restores -> builds -> publishes -> validates `Resources` -> creates ZIP -> runs Inno Setup -> removes staging folder.

### GitHub Release Checklist

Before tagging a release:

- [ ] Run `.\build.ps1 -CreatePortableZip` and verify both artifacts appear in `Publish\`
- [ ] Test on a **clean Windows machine** (no dev tools installed):
  - App starts without the .NET SDK
  - **Settings > Resource Status** reports all core tools as ready
  - Image, video/audio, PDF/document conversions complete successfully
  - Missing resource warnings show the correct expected paths
- [ ] Version in `CortexFX.csproj` matches the release tag
- [ ] `dotnet restore` reports no unresolved high-severity NuGet vulnerabilities

### Release Artifacts

| Artifact | Description |
|---|---|
| `Publish\CortexFX_Setup_v1.6.0.exe` | Windows installer |
| `Publish\CortexFX_Portable_v1.6.0-win-x64.zip` | Self-contained portable build |

---

## Troubleshooting

### Missing Resources

Open **Settings > Resource Status**. Core tools must be in the `Resources` folder next to `CortexFX.exe`. If a file is missing, place it at the exact path shown in the app.

### Failed Conversions

- Verify the input file opens in its source application.
- Use a writable output folder.
- Do not convert directly over the original file.
- For large batches, split into smaller groups.

### Office & PDF Issues

Microsoft Office must be **installed and activated** for Office COM conversions. PDF-to-Word and PDF-to-PowerPoint depend on Word's built-in PDF import and may vary by Office version.

### Optional Tool Issues

LibreOffice, 7-Zip, and Calibre require their executables in `Resources` or their standard `Program Files` install locations.

### Logs

Cortex FX writes logs to:

```
%LOCALAPPDATA%\Cortex FX\Logs
```

Open **Settings > Open Log Folder** to browse the current log file. Logs include: startup checks, resource validation, conversion start/end, external process failures, and unexpected exceptions.

---

## Known Limitations

- **Office COM automation** can be slow with very large files. Cortex FX serializes Office work to reduce "server busy" errors.
- **PDF -> XLSX** is not enabled — reliable table extraction requires a dedicated engine.
- **JPG/PNG -> SVG** is not supported — vector tracing requires a tracing engine and output quality varies.
- **HEIC/HEIF** support depends on the ImageMagick build and installed delegates.
- **Calibre, LibreOffice, and 7-Zip** are optional and must be installed or bundled separately.
- Review NuGet audit warnings before each public release.

---

## License

License information will be added here.

---

<div align="center">

Built with .NET 10 · WPF · FFmpeg · ImageMagick

</div>
