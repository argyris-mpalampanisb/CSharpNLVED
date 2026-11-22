# CSharpNLVED

A small, portable, free and open-source video editor (alpha, expect bugs) for the everyday job:
put clips in order, trim them, set the volume per track, and export fast,
using your GPU encoder (NVENC, AMF, Quick Sync, VideoToolbox, VAAPI).

CSharpNLVED is GPU-only by design. It tests which GPU encoders really work, re-tests
automatically whenever FFmpeg, the kernel or a GPU driver changes, and when
something is broken it tells you why (e.g. "driver/library mismatch, reboot")
instead of silently falling back to slow CPU encoding.

Runs on Windows, macOS and Linux. Built with C# / .NET 11 and Avalonia UI.
Uses FFmpeg, which it downloads and keeps up to date by itself.

## Status

Early development. Roadmap:

1. [x] App skeleton, cross-platform builds
2. [x] FFmpeg manager (download + update check on launch)
3. [x] GPU encoder detection with automatic re-check and Linux driver diagnostics
4. [x] Project model (tracks, clips, per-track volume, undo/redo), saved as `.csnlved` JSON
5. [x] GPU export with progress and cancel (plus a basic "Add clips…")
6. [ ] Media import (ffprobe, thumbnails)
7. [ ] Timeline UI
8. [ ] Preview playback
9. [ ] App self-update from GitHub releases

## Building

Requires the [.NET 11 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project src/CSharpNLVED.App
```

Run the tests:

```sh
dotnet test
```

Portable build for your OS (examples):

```sh
dotnet publish src/CSharpNLVED.App -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o out/win-x64
dotnet publish src/CSharpNLVED.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out/linux-x64
dotnet publish src/CSharpNLVED.App -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o out/osx-arm64
```

Pushing a tag like `v0.1.0` to GitHub builds all platforms and attaches them to a release.

## Where FFmpeg comes from

On launch CSharpNLVED checks for the newest FFmpeg build for your system and offers to
download it into its own data folder (`data/tools/ffmpeg/bin`):

| OS      | Source                                                        |
|---------|---------------------------------------------------------------|
| Windows | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) full build (via its GitHub mirror, GyanD/codexffmpeg) |
| Linux   | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) static GPL build, newest release branch |
| macOS   | [Martin Riedl's build server](https://ffmpeg.martin-riedl.de) (Apple Silicon and Intel) |

If you already have FFmpeg on your PATH, CSharpNLVED can use it, but its own copy is recommended.

## Portable data

CSharpNLVED stores its settings, downloaded tools and caches in a `data` folder next
to the executable. If that folder isn't writable, it falls back to your user
app-data folder.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).

CSharpNLVED is an independent project. It is not affiliated with or endorsed by
Microsoft or the FFmpeg project. C# and .NET are named only to describe what it is
built with.
