# Audio Studio

Open an audio file using Audio Cutter. Drag across the waveform to select a range;
drag within 8 pixels of either selection edge to refine it. Click the waveform to
seek. Set in / Set out place a marker at the playhead. Select all resets the range.
Space plays or stops; I / O set the in / out points; Ctrl+A selects the whole file.
Loop repeats the selection. The volume control affects preview only.
Export cut offers the existing audio format choices and preserves source volume.

## Local vocals / music separation

Choose Full track or Selection, click Separate audio, then choose an output parent
folder. The included UVR-MDX-NET-Voc_FT model runs locally on the selected processor through
sherpa-onnx / ONNX Runtime. The CPU mode needs no GPU. Neither mode needs Python, an account, network access or model downloads in this distribution. Audio never leaves the device.

The service decodes the chosen range into stereo 44.1 kHz PCM16, runs bounded
30-second chunks with 2 seconds of context on both sides, and discards the context
when assembling the stems. Native inference runs in a tracked child process;
cancellation terminates the process without blocking the WPF dispatcher.
Progress reports completed chunks, rather than estimating inference time.

On success a new `<source>_Stems` folder contains `Vocals.mp3` and `Instrumental.mp3` by default. Select WAV to export `Vocals.wav` and `Instrumental.wav` instead.
Name collisions get a numeric suffix. Both selected-format stems are finalized before the output
folder is published by an atomic directory rename. Failed / cancelled jobs do not
publish incomplete stems. The source and existing outputs are never overwritten.
Use Vocals / Music to load the corresponding stem into the same editor for
listening and further trimming. Open output folder shows both files in Explorer.

Separation is an AI estimate: vocal bleed and musical artifacts can remain.
Results depend on the recording. Two-second context reduces chunk edge artifacts
but does not guarantee perfectly seamless stems for every source.

## Distribution and attribution

Keep `Resources/AudioSeparation/` beside the app. The csproj includes all its files
in build and publish output. It contains the Windows x64 CPU executable, model,
ONNX Runtime libraries, attribution, MIT / Apache licenses, and a SHA256 manifest.
A missing engine disables separation with a visible explanation; trimming still
works. The CPU engine and model use about 82 MiB. The optional GPU add-on is described below.

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
folder. Keep it present in GPU distributions. Its files are included by the existing
recursive AudioSeparation content rule in the csproj.

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