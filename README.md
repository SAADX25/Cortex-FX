# Cortex FX

Cortex FX is a Windows WPF desktop application for local file conversion. It is designed to keep user files on the machine and route work through local engines such as FFmpeg, ImageMagick, Microsoft Office automation, and optional tools like LibreOffice, 7-Zip, and Calibre.

## Features

- Image conversion: JPG, PNG, WEBP, BMP, ICO, TIFF, GIF, HEIC/HEIF when supported by the installed ImageMagick build.
- Video and audio conversion through FFmpeg: MP4, MKV, MOV, AVI, WEBM, MP3, WAV, AAC, FLAC, OGG, M4A.
- Document conversion: PDF, DOCX, DOC, PPTX, PPT, XLSX, XLS, ODT, RTF, TXT.
- PDF tools: PDF to image and image merge to PDF.
- Archive conversion through optional 7-Zip: ZIP, RAR input, 7Z, TAR.
- E-book conversion through optional Calibre: EPUB, MOBI, AZW3, PDF.
- Local-only processing by default. Files are not uploaded to an online service.
- Console logging when launched from a terminal, without opening a console window for normal desktop launches.

## Requirements

- Windows 10 or later.
- .NET SDK/runtime compatible with `net10.0-windows` for development.
- Microsoft Office is required for Office COM conversions such as PDF to Word and Office documents to PDF.
- External command-line tools must be shipped in the `Resources` folder or installed in supported system locations.

## Required Resources

The application expects these core tools under the app output directory:

```text
Resources/
  ffmpeg.exe
  magick.exe
  pdftocairo.exe
  ffmpeg_libs/
    avcodec-58.dll
    ...
```

Optional engines:

```text
Resources/
  LibreOffice/program/soffice.exe
  7z.exe
  7za.exe
  Calibre/ebook-convert.exe
```

The app resolves resources in this order:

1. `AppContext.BaseDirectory/Resources` for installed and published builds.
2. Project `Resources` folder only when running a Debug build from the repository.

If required resources are missing, Cortex FX shows a user-friendly warning that includes the expected path.

## Build

Restore and build:

```powershell
dotnet restore .\CortexFX.csproj
dotnet build .\CortexFX.csproj
```

Run from source:

```powershell
dotnet run --project .\CortexFX.csproj
```

Or use the portable developer launcher:

```powershell
.\Cortex_FX.bat
```

## Publish

Publish manually:

```powershell
dotnet publish .\CortexFX.csproj -c Release -r win-x64 --self-contained true -o .\Publish\CortexFX_v0.6.0
```

Publish using the build script:

```powershell
.\build.ps1
```

Skip installer creation:

```powershell
.\build.ps1 -SkipInstaller
```

The build script uses `$PSScriptRoot`, so it can run from any developer machine after cloning the repository.

## Installer

The optional installer is built with Inno Setup 6. If Inno Setup is not installed, `build.ps1` still publishes the app and reports that installer creation was skipped.

The installer script is `setup.iss` and uses paths relative to the repository or paths passed by `build.ps1`.

## Known Limitations

- Microsoft Office COM automation can be slow or unstable with very large files. The Office pipeline is serialized and STA-safe, but Office itself remains a desktop automation dependency.
- PDF to XLSX is intentionally not enabled because reliable table extraction requires a dedicated extraction engine.
- JPG/PNG to SVG is not a simple conversion; vector tracing requires tools such as Potrace or Inkscape and results vary by image.
- HEIC/HEIF support depends on the ImageMagick build and installed delegates.
- Calibre, LibreOffice, and 7-Zip are optional and must be installed or bundled to enable their related conversions.
- Current package audit warnings identify vulnerabilities in the installed Magick.NET package version. These warnings are intentionally not hidden.

## Screenshots

Screenshots will be added here.

## License

License information will be added here.
