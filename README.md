# Cortex FX

Cortex FX is a Windows WPF desktop app for local file conversion. It keeps files on the user's machine and routes work through local tools such as FFmpeg, ImageMagick, Microsoft Office automation, and optional engines like LibreOffice, 7-Zip, and Calibre.

## Quick Start

1. Download the latest installer or portable ZIP from GitHub Releases.
2. Install the app, or extract the ZIP to a writable folder.
3. Make sure the `Resources` folder is beside `CortexFX.exe`.
4. Open Cortex FX, choose a conversion tool, add files, select an output folder, and click **Convert**.

## Download

Published builds should be attached to each GitHub Release:

- `CortexFX_Setup_vX.Y.Z.exe` for a normal Windows installer.
- `CortexFX_Portable_vX.Y.Z-win-x64.zip` for a portable build.

If Windows SmartScreen warns on first launch, choose **More info** and **Run anyway** only if the file came from the official release page.

## Features

- Image conversion: JPG, PNG, WEBP, BMP, ICO, TIFF, GIF, HEIC/HEIF when supported by the bundled ImageMagick build.
- Video and audio conversion through FFmpeg: MP4, MKV, MOV, AVI, WEBM, MP3, WAV, AAC, FLAC, OGG, M4A.
- Document conversion: PDF, DOCX, DOC, PPTX, PPT, XLSX, XLS, ODT, RTF, TXT.
- PDF tools: PDF to image and image merge to PDF.
- Archive conversion through optional 7-Zip: ZIP, RAR input, 7Z, TAR.
- E-book conversion through optional Calibre: EPUB, MOBI, AZW3, PDF.
- Local logging under the user's local app data folder for troubleshooting.

## Required External Tools

The app expects this core layout in the published app folder:

```text
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

Optional engines can also be bundled:

```text
Resources/
  LibreOffice/program/soffice.exe
  7z.exe
  7za.exe
  Calibre/ebook-convert.exe
```

Resource resolution order:

1. `{AppContext.BaseDirectory}\Resources` for installed and published builds.
2. The project `Resources` folder when running a Debug build from source.

Open **Settings > Resource Status** in the app to see the exact folder and missing files.

## Build From Source

Requirements:

- Windows 10 or later.
- .NET SDK compatible with `net10.0-windows`.
- Inno Setup 6, only if you want to build the installer.
- Microsoft Office, only for Office COM conversion features.

Restore and build:

```powershell
dotnet restore .\CortexFX.csproj
dotnet build .\CortexFX.csproj -c Release
```

Run from source:

```powershell
dotnet run --project .\CortexFX.csproj
```

## Publish

Manual publish:

```powershell
dotnet publish .\CortexFX.csproj -c Release -r win-x64 --self-contained true -o .\Publish\CortexFX_v1.0.0
```

Use the build script:

```powershell
.\build.ps1
```

By default, the build script uses `Publish\CortexFX_vX.Y.Z` as a temporary staging folder and removes it after the installer is created. A normal installer build leaves only the setup executable in `Publish`.

Create a portable ZIP:

```powershell
.\build.ps1 -CreatePortableZip
```

When `-CreatePortableZip` is used, the final `Publish` folder contains the installer and portable ZIP only.

Keep the staging folder for debugging:

```powershell
.\build.ps1 -KeepStagingFolder
```

Publish without trying to build the installer:

```powershell
.\build.ps1 -SkipInstaller -CreatePortableZip
```

The project file copies `Resources\**\*.*` into both build and publish output. The Inno Setup script installs the full publish directory recursively, including `Resources` when present.

## GitHub Release Checklist

Before tagging a release:

```powershell
dotnet restore .\CortexFX.csproj
dotnet build .\CortexFX.csproj -c Release --no-restore
dotnet publish .\CortexFX.csproj -c Release -r win-x64 --self-contained true -o .\Publish\CortexFX_v1.0.0
```

Recommended release build:

```powershell
.\build.ps1 -CreatePortableZip
```

This command restores, builds, publishes to a temporary staging folder, validates required `Resources` files, creates a portable ZIP, builds the installer when Inno Setup 6 is installed, and removes the staging folder after both release artifacts are created. Use `-KeepStagingFolder` when you need to inspect the staged app files.

Each public release should include:

- Installer: `Publish\CortexFX_Setup_vX.Y.Z.exe`.
- Portable ZIP: `Publish\CortexFX_Portable_vX.Y.Z-win-x64.zip`.
- A note that `Resources` is bundled and must remain beside `CortexFX.exe` in portable installs.
- Version number matching `CortexFX.csproj` and the release tag. `build.ps1` passes this version into `setup.iss`.
- Known limitations for Office, HEIC/HEIF, optional engines, and very large files.

Before uploading, test on a clean Windows machine or VM:

- App starts without development tools installed.
- Settings > Resource Status reports core tools ready.
- Image conversion works.
- Video/audio conversion works.
- PDF/document conversion behavior is clear, including Office-required warnings.
- Missing resource warnings name the missing files and expected folder.
- NuGet restore does not report unresolved high-severity package vulnerabilities.

## Troubleshooting

### Missing Resources

Open **Settings > Resource Status**. Core tools must be in the published `Resources` folder next to the app executable. If a tool is missing, place the named file in the location shown by the app.

### Failed Conversions

Check that the input file opens normally in its source application. Choose a writable output folder and avoid converting directly over the original file. For large batches, split work into smaller groups.

### Office and PDF Issues

Microsoft Office must be installed, activated, and able to open the document for Office COM conversions. PDF to Word and PDF to PowerPoint depend on Word's PDF import behavior and may vary by Office version.

### Optional Tool Issues

LibreOffice, 7-Zip, and Calibre conversions require their executables either in `Resources` or in their standard Program Files install locations.

### Logs

Cortex FX writes logs to:

```text
%LOCALAPPDATA%\Cortex FX\Logs
```

Open **Settings > Open Log Folder** to find the current log file. Logs include startup checks, resource validation, conversion start/end, external process failures, and unexpected exceptions.

## Known Limitations

- Microsoft Office COM automation can be slow or fragile with very large files. Cortex FX serializes Office work to reduce "server busy" errors.
- PDF to XLSX is not enabled because reliable table extraction requires a dedicated extraction engine.
- JPG/PNG to SVG is not a simple conversion; vector tracing requires a tracing engine and results vary by image.
- HEIC/HEIF support depends on the ImageMagick build and installed delegates.
- Calibre, LibreOffice, and 7-Zip are optional and must be installed or bundled to enable their related conversions.
- Package audit warnings should be reviewed before public release.

## License

License information will be added here.
