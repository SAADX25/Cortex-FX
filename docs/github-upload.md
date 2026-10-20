# Preparing Cortex FX for GitHub

The repository contains source code, tests, app icons, build tooling, dependency
locks, documentation and upstream licenses/notices. Runtime executables, DLLs,
model weights, downloaded archives, local settings/logs and build output stay out
of Git. Ignoring a file does not remove the local copy needed to run the app.

## What belongs in a commit

- `*.cs`, `*.xaml`, project/solution files, `Assets/logo.ico` and CI configuration.
- `tools/`, build/launch scripts, `.gitignore`, `.gitattributes` and `docs/`.
- `Resources/README.txt`, `Resources/AudioSeparation/manifest.json`, all NOTICE,
  LICENSE and ThirdPartyNotices text files, including the GPU licenses.
- `tools/audio-resources.lock.json`: official download URLs and pinned SHA256 hashes.

## What belongs in Releases

- The complete installer or portable ZIP, including its required Resources tree.
- Model weights such as `UVR-MDX-NET-Voc_FT.onnx` and optional GPU runtime packs,
  if distributing them separately. Retain the relevant notices/licenses in each pack.
- Do not add `.exe`, `.dll`, `.onnx`, ZIPs, `bin/`, `obj/`, `Publish/`, logs,
  signing keys/certificates or credentials to a source commit.

This project uses Releases/download restoration for its native dependencies,
not Git LFS. `tools/Test-Repository.ps1` enforces a 50 MiB source-file policy
and rejects runtime binaries even when a smaller file is force-added.

## Build after cloning

Install a supported .NET 10 SDK on Windows x64, then run:

```powershell
dotnet restore CortexFX.slnx
dotnet test CortexFX.slnx -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Restore-AudioResources.ps1
```

The audio setup restores the CPU engine and UVR model using version-pinned official
archives and verifies archive/extracted-file SHA256 values. Existing matching
files are kept. Downloads are cached under `%LOCALAPPDATA%/CortexFX/DependencyCache`.
For the optional NVIDIA GPU files:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Restore-AudioResources.ps1 -IncludeGpu
```

The GPU setup needs a 64-bit shell, about 2.15 GiB of runtime files, additional
archive/cache space, and Microsoft Visual C++ x64 runtime DLLs available either in
project Resources or Windows System32. The helper copies verified Microsoft
runtime DLLs locally; it does not install software, drivers or change PATH.
Newer signed Microsoft runtime revisions are accepted; known vendor/model hashes
stay pinned. A compatible NVIDIA driver/device is required to use GPU inference.

`Resources/README.txt` lists the other media/PDF/Office tools. They are obtained
separately; the audio setup does not install FFmpeg, ImageMagick or Poppler.
Without native tools, the app still compiles and its pure unit tests run, while
native/Office/model integration tests are skipped when their prerequisites are absent.

Read-only audio validation (no downloads or writes):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Restore-AudioResources.ps1 -VerifyOnly -IncludeGpu
```

Use `-Offline -CacheDirectory <folder>` for a restoration using already verified
cached archives only. Corrupt/missing cache entries fail instead of using the network.

## Validate the upload

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-Repository.ps1
git diff --cached --check -- . ':(exclude)Resources/AudioSeparation/**'
git status --short
```

`Test-Repository.ps1` reads the Git index, checks tracked files (or changed indexed
files with `-StagedOnly`), blocks generated/binary/private paths and oversized files,
and scans for common credential patterns without printing secret values. CI runs
the same source check before build/test. This is a focused upload check, not a
complete security audit or proof that every possible secret/defect is absent.

## Prepare a release

```powershell
# Smaller CPU distribution (GPU option is unavailable until the add-on is installed):
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -CpuOnly -SkipInstaller -CreatePortableZip
# Complete CPU/GPU distribution, with Inno Setup 6 when installed:
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -CreatePortableZip
```

Every build creates a fresh release directory under `Publish/` and an `app/` child.
Existing release directories are preserved. Repeated builds of the same version
get a timestamped directory. The installer keeps the exact name
`CortexFX_Setup_v<version>.exe` required by the app updater. Choose one installer
per release; the CPU portable ZIP has an additional `-cpu` suffix.

A direct CPU-only publish can use `-p:IncludeAudioGpu=false`; the default profile
keeps the existing behavior and includes an installed GPU add-on. Integration tests
are opt-in via the variables documented in `docs/audio-studio.md`.

All source checks here are local. Commit, push, release creation and artifact upload
remain manual actions for the repository owner.