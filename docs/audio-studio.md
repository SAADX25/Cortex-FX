# Audio Studio

The home screen has separate **Audio Cutter** and **AI Audio Separator** tools.
Audio Cutter shows trimming controls. AI Audio Separator has a dedicated purple
workspace with a song picker/drop target, waveform preview and two stem players.
The separator waveform has a moving playhead and a shaded played region. Click
the waveform or drag the playhead to seek; arrow keys move five seconds. Stopping
and restarting the separator preview preserves the chosen position. Seeking
flushes queued audio so playback continues from the newly selected point.
The speaker button mutes/unmutes previews; the adjacent 0–100% volume slider
controls both the source preview and the vocals/instrumental stem previews.
Changing the slider restores sound after muting. This does not change exported
audio levels or the source file.
In Audio Cutter, drag across the waveform to select a range;
drag within 8 pixels of either selection edge to refine it. Click the waveform to
seek. Set in / Set out place a marker at the playhead. Select all resets the range.
Space plays or stops; I / O set the in / out points; Ctrl+A selects the whole file.
Loop repeats the selection. The volume control affects preview only.
Export cut in Audio Cutter offers the existing audio format choices and preserves source volume.

## Local vocals / music separation

Choose a song (or drop an audio file), select MP3/WAV and use the main action.
Before setup, this single button says **Download AI engine**. After setup, it
becomes **Separate vocals & music**; choose an output folder to save both stems.
The complete song is processed; trimming remains in Audio Cutter.
The UVR-MDX-NET-Voc_FT model runs locally through sherpa-onnx / ONNX Runtime.
Standard processing needs no GPU. NVIDIA acceleration is an opt-in choice under
**Advanced options**. Selecting it changes the same main button to
**Enable GPU acceleration** when additional files are needed. There are no separate
CPU/GPU download buttons in the primary interface.
Downloads require internet; processing afterwards works offline without Python
or an account. Audio never leaves the device.

The service decodes the chosen range into stereo 44.1 kHz PCM16, runs bounded
30-second chunks with 2 seconds of context on both sides, and discards the context
when assembling the stems. Native inference runs in a tracked child process;
cancellation terminates the process without blocking the WPF dispatcher.
Progress reports completed chunks, rather than estimating inference time.

On success a new `<source>_Stems` folder contains `Vocals.mp3` and `Instrumental.mp3` by default. Select WAV to export `Vocals.wav` and `Instrumental.wav` instead.
Name collisions get a numeric suffix. Both selected-format stems are finalized before the output
folder is published by an atomic directory rename. Failed / cancelled jobs do not
publish incomplete stems. The source and existing outputs are never overwritten.
Use Vocals / Instrumental to preview or stop each stem within the purple workspace.
Starting another preview stops the previous track. Open output folder shows both
files in Explorer; exported files can also be opened in Audio Cutter for trimming.

Separation is an AI estimate: vocal bleed and musical artifacts can remain.
Results depend on the recording. Two-second context reduces chunk edge artifacts
but does not guarantee perfectly seamless stems for every source.

## Distribution and attribution

The default build/publish excludes the large AI engine, model and CUDA libraries.
It keeps notices, licenses, the pinned download manifest and small Microsoft
runtime support DLLs when present. Downloads install under
`%LOCALAPPDATA%/CortexFX/AudioSeparation`, avoiding writes to the app installation.
The workspace fills the available window height; the source waveform and stem
players grow with it. Decorative headings and marketing footnotes are omitted.
Archive and extracted-file SHA256 hashes are verified before publishing an
installation. Cancellation/failure removes temporary downloads and preserves the
previous installation. A download bar shows the current file's percentage and
received/total MB when the server supplies its size; otherwise it animates and
shows received MB. Verification/extraction uses an animated bar rather than a
download percentage. This percentage is per file, not the whole multi-file setup.
use Cancel download to stop, then retry when ready. Temporary archives and
extracted files need additional free disk space during setup.

For offline distributions, `build.ps1 -CpuOnly` bundles the CPU files, while
`build.ps1 -BundleAudioAi` bundles CPU and GPU. Direct MSBuild equivalents are
`-p:IncludeAudioAi=true -p:IncludeAudioGpu=false` or both properties set to `true`.
Bundled files remain supported; the downloaded installation takes precedence.
The CPU engine and model use about 82 MiB. The optional GPU add-on is described below.

- UVR and its developers (model): https://github.com/Anjok07/ultimatevocalremovergui
- sherpa-onnx v1.13.8: https://github.com/k2-fsa/sherpa-onnx/releases/tag/v1.13.8
- Model distribution: https://github.com/k2-fsa/sherpa-onnx/releases/tag/source-separation-models
- ONNX Runtime v1.28.2: https://github.com/microsoft/onnxruntime

## Validation

Set CORTEXFX_TEST_FFMPEG, CORTEXFX_TEST_SEPARATION_DIR and
CORTEXFX_TEST_AUDIO_SAMPLE to enable the real model integration test. The latter
can point to sherpa-onnx's published audio_example.wav sample. The test verifies
selected range duration, different nonempty stereo stems, name collisions,
original SHA256, and temporary output cleanup. Other tests check exact chunk
sample coverage / reconstruction and cancellation / inference failure cleanup.

Runtime tests in this session use Windows 11 x64. Windows 10 has not been tested
on a separate machine or VM; do not infer a complete Windows 10 validation from
these checks.
## Processor and output options

CPU is the default and works independently of the GPU add-on. GPU uses NVIDIA CUDA
on the first NVIDIA device exposed by the driver. Hardware detection runs off the
WPF dispatcher. The app disables GPU selection when the driver/device or required
GPU files are missing and explains this in the option tooltip. An explicit GPU
job that cannot start CUDA fails with a CPU retry suggestion; a logged CPU fallback
is rejected rather than presented as successful GPU processing.

The optional `Resources/AudioSeparation/Gpu/` folder includes the Windows x64
CUDA 12 inference executable, CUDA Runtime 12.8.90, cuBLAS 12.8.4.1 and cuDNN
9.10.2.21 libraries, existing Microsoft app-local runtime redistributables, and
licenses. cuDNN 9.10.2 retains Pascal / GTX 1050 Ti support. No system installations,
PATH changes, driver updates, Python packages or account setup are performed.
The GPU add-on is approximately 2.15 GiB; CPU-only packages can omit the entire Gpu
folder. Keep it present in offline GPU distributions. Large GPU files are bundled
only when both `IncludeAudioAi` and `IncludeAudioGpu` are explicitly true.

MP3 stereo 192 kbps is the default export. WAV remains an uncompressed option.
The app estimates size per stem from the selected range and codec, and displays
the actual sizes and output format after success. For a 223.21-second track, each
MP3 is about 5.1 MiB instead of about 37.6 MiB for PCM16 WAV. MP3 is lossy; choose
WAV when retaining uncompressed samples matters. Intermediate WAVs stay temporary;
only the two selected-format outputs are published. If either encoder fails or is
cancelled, neither result is published and previous exports remain untouched.

Set CORTEXFX_TEST_GPU=1 in addition to the other test variables to enable real CUDA
inference testing. The GPU integration check uses actual NVIDIA hardware and the
bundled CUDA engine, verifies the reported processor, and decodes both MP3 outputs.
Separate tests cover absent GPU hardware, rejected CPU fallback, MP3 duration/size,
encoding failure cleanup, and the existing WAV/chunk pipeline.
