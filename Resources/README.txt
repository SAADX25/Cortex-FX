Cortex FX local tools
=====================

Source commits contain code, notices, licenses and dependency locks.
Runtime binaries, weights and build output are ignored by Git and remain local.
The complete release installer/portable package must include its required tools.

Core media/PDF tools (obtain separately and retain their redistribution notices):
  ffmpeg.exe
  magick.exe and the required ImageMagick/delegate DLLs
  pdftocairo.exe and its Poppler companion DLLs
  ffmpeg_libs/avcodec-58.dll
  ffmpeg_libs/avformat-58.dll
  ffmpeg_libs/avutil-56.dll
  ffmpeg_libs/swresample-3.dll
  ffmpeg_libs/swscale-5.dll

FFME video preview uses FFmpeg 4.4 shared libraries (avcodec-58, etc.).
See README.md for upstream tool links and Settings > Resource Status for paths.
Microsoft Office and optional OCR/archive/e-book engines have separate prerequisites.

Local audio separation:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools/Restore-AudioResources.ps1
  Add -IncludeGpu for NVIDIA GPU runtime files.
  Add -VerifyOnly to check existing files without downloads or writes.

Run those commands from the project root. The helper uses official upstream URLs
and pinned SHA256 values from tools/audio-resources.lock.json. Models and DLLs are
not committed. Preserve AudioSeparation/*LICENSE*, *NOTICE*, ThirdPartyNotices,
manifest.json and Gpu/Licenses in source and release distributions.

Details: docs/github-upload.md and docs/audio-studio.md.