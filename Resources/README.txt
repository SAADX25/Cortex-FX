Cortex FX local tools
=====================

Place the following core files in this folder (or run setup so they are downloaded):

  ffmpeg.exe
  magick.exe
  pdftocairo.exe
  ffmpeg_libs\avcodec-58.dll
  ffmpeg_libs\avformat-58.dll
  ffmpeg_libs\avutil-56.dll
  ffmpeg_libs\swresample-3.dll
  ffmpeg_libs\swscale-5.dll

Notes:
- FFME video preview requires FFmpeg 4.4 shared libraries (avcodec-58, etc.).
- pdftocairo.exe needs its companion DLLs beside it in this folder.
- Binary files in this folder are gitignored; keep them locally for Debug/Release runs.
