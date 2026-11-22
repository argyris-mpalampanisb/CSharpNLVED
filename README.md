<div align="center">

# CSharpNLVED

**A small, free, GPU-only video editor for everyday cutting.**

Cut and arrange clips, add text and fades, balance your tracks, and export on your graphics card.

[![Build](https://github.com/argyris-mpalampanisb/CSharpNLVED/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/argyris-mpalampanisb/CSharpNLVED/actions/workflows/build.yml)
[![License: GPL v3 or later](https://img.shields.io/badge/license-GPLv3%2B-blue)](LICENSE)
[![Status: alpha](https://img.shields.io/badge/status-alpha-orange)](#roadmap)
[![.NET 11](https://img.shields.io/badge/.NET-11-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download)
[![Avalonia UI 12](https://img.shields.io/badge/Avalonia_UI-12-8B44AC)](https://avaloniaui.net/)
[![Powered by FFmpeg](https://img.shields.io/badge/powered_by-FFmpeg-007808?logo=ffmpeg&logoColor=white)](https://ffmpeg.org/)

[Features](#features) •
[Getting started](#getting-started) •
[Shortcuts](#shortcuts) •
[Roadmap](#roadmap) •
[Building](#building-from-source) •
[Contributing](#contributing)

</div>

---

> [!WARNING]
> **CSharpNLVED is in alpha, so expect bugs.** Keep copies of important projects.
> There is no video preview or playback yet.

## Why CSharpNLVED?

Hardware encoding in video editors can quietly break after a driver, kernel or system update. CSharpNLVED is built around that problem:

- **It tests instead of guessing.** Each GPU encoder gets a real test encode.
- **It notices changes.** Encoders are tested again when FFmpeg, the kernel or a GPU driver changes.
- **It explains failures.** You get a plain-language reason, such as *"driver/library mismatch, reboot"*.
- **It never falls back to the CPU.** If no GPU encoder works, export is disabled and you're told why.

## Features

| | Feature | What you get |
|:---:|---|---|
| 🎬 | **Timeline** | Multiple video and audio tracks, move, trim, split, sort, snapping and zoom |
| 🔗 | **Linked clips** | Video and its sound edit together, or unlink them to move separately |
| 🧲 | **Auto-ripple** | Optionally close or open gaps as you delete and trim (off by default) |
| 🌗 | **Fades** | Drag a clip's top corners to fade in or out |
| 🔤 | **Text** | Font, size, colors, outline and position, with a live preview of the text |
| 🔊 | **Audio** | Volume for each track, from 0 to 200% |
| 📂 | **Import** | Pick files, or drag and drop files and whole folders |
| ⚡ | **Export** | MP4, MKV or MOV with three quality presets, progress, cancel and logs |
| ↩️ | **Undo and redo** | For your timeline edits, with a whole drag undone in one step |
| 💾 | **Projects** | Readable `.csnlved` JSON files |
| 🎒 | **Portable** | Keeps its data next to the app |

## Platform support

| Platform | GPU encoders | Codecs | Status |
|---|---|---|---|
| **Windows** x64 | NVENC, AMF, Quick Sync | H.264, HEVC, AV1\* | ✅ Main development platform |
| **Linux** x64 | NVENC, VAAPI, Quick Sync | H.264, HEVC, AV1\* | 🧪 Builds, untested |
| **macOS** Apple Silicon and Intel | VideoToolbox | H.264, HEVC | 🧪 Builds, untested |

\* Only on GPUs that can encode the codec. For example, NVENC AV1 needs an RTX 40-series card or newer.

## Getting started

### Requirements

- A GPU with a working hardware encoder and an up-to-date driver
- FFmpeg 7 or newer, which the app offers to download for you

### Download

There are no releases yet. To try a development build:

1. Open the [latest successful builds](https://github.com/argyris-mpalampanisb/CSharpNLVED/actions/workflows/build.yml?query=branch%3Amain+is%3Asuccess) and select the top run.
2. Download the artifact for your system (you need to be signed in to GitHub).
3. Extract it, then extract the archive inside, and run `CSharpNLVED`.

The builds are self-contained, so you don't need to install .NET.

### Your first video

1. **Import:** drag your clips onto the timeline.
2. **Cut:** move the playhead and press <kbd>S</kbd> to split, or drag a clip's edges to trim it.
3. **Finish:** drag the top corners for fades, and press <kbd>T</kbd> to add text.
4. **Export:** press <kbd>Ctrl</kbd> + <kbd>E</kbd>.

## Shortcuts

> [!TIP]
> On macOS, use <kbd>⌘ Cmd</kbd> wherever <kbd>Ctrl</kbd> is shown.

| Editing | Keys |
|---|---|
| Split at the playhead | <kbd>S</kbd> |
| Unlink, or link the selected clips | <kbd>U</kbd> |
| Add text at the playhead | <kbd>T</kbd> |
| Remove the selected clips | <kbd>Delete</kbd> or <kbd>Backspace</kbd> |
| Undo | <kbd>Ctrl</kbd> + <kbd>Z</kbd> |
| Redo | <kbd>Ctrl</kbd> + <kbd>Y</kbd> or <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>Z</kbd> |
| Jump to the start or end | <kbd>Home</kbd> or <kbd>End</kbd> |

<details>
<summary><b>File shortcuts</b></summary>

| File | Keys |
|---|---|
| New project | <kbd>Ctrl</kbd> + <kbd>N</kbd> |
| Open project | <kbd>Ctrl</kbd> + <kbd>O</kbd> |
| Save | <kbd>Ctrl</kbd> + <kbd>S</kbd> |
| Save as | <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>S</kbd> |
| Add clips | <kbd>Ctrl</kbd> + <kbd>I</kbd> |
| Export | <kbd>Ctrl</kbd> + <kbd>E</kbd> |

</details>

<details>
<summary><b>Mouse controls</b></summary>

| Action | Mouse |
|---|---|
| Select several clips | <kbd>Ctrl</kbd> + click |
| Move a clip, also between tracks of the same kind | Drag it |
| Move without snapping | Hold <kbd>Alt</kbd> while dragging |
| Trim | Drag the left or right edge |
| Fade in or out | Drag the top-left or top-right corner |
| Move the playhead | Click or drag on the ruler or empty space |
| Zoom at the pointer | Mouse wheel |
| Scroll the timeline | <kbd>Shift</kbd> + mouse wheel |
| Edit text | Double-click a text clip |
| Add media to the end of the timeline | Double-click it in the Media panel |
| Reset a track's volume to 100% | Double-click its slider |

</details>

## Roadmap

- [x] Multitrack timeline with linked video and audio
- [x] GPU export
- [x] Fades
- [x] Text clips
- [ ] **Transitions (in progress)**
  - [x] Project model and editing rules
  - [ ] Export
  - [ ] Timeline controls
  - [ ] Built-in gradient wipes, including a soft cloud wipe
- [ ] GPU decoding for faster export
- [ ] Thumbnails and waveforms on clips
- [ ] Preview of the frame at the playhead
- [ ] Large time counter
- [ ] Media bin with thumbnails
- [ ] Full preview playback
- [ ] App self-update from GitHub releases

### Known limitations

- HDR video isn't tone-mapped.
- Very long projects (roughly 50 clips or more) may exceed the Windows command-line length limit on export.
- Moving a linked video to another track doesn't move its sound to another audio track.

## Building from source

You need [Git](https://git-scm.com/) and the [.NET 11 SDK](https://dotnet.microsoft.com/download).

```sh
git clone https://github.com/argyris-mpalampanisb/CSharpNLVED.git
cd CSharpNLVED
dotnet run --project src/CSharpNLVED.App
```

Run the tests:

```sh
dotnet test
```

<details>
<summary><b>Create a portable build</b></summary>

Replace `win-x64` with `linux-x64`, `osx-arm64` or `osx-x64` as needed:

```sh
dotnet publish src/CSharpNLVED.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out/win-x64
```

Pushing a tag like `v0.1.0` makes GitHub Actions build all four targets and attach them to a release.

</details>

<details>
<summary><b>Project structure</b></summary>

```text
src/
├── CSharpNLVED.Core/         Editing, projects, FFmpeg, GPU detection, export (no UI)
└── CSharpNLVED.App/          Avalonia user interface
tests/
└── CSharpNLVED.Core.Tests/   xUnit v3 tests
```

</details>

## How it works

CSharpNLVED runs `ffmpeg` and `ffprobe` as separate programs and doesn't link to their libraries, so FFmpeg can be updated independently of the app.

<details>
<summary><b>Where FFmpeg comes from</b></summary>

On launch, the app checks for the newest build for your system and offers to download it into `data/tools/ffmpeg/bin`.

| System | Source |
|---|---|
| Windows | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) full build, through its [GitHub mirror](https://github.com/GyanD/codexffmpeg) |
| Linux | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) static GPL build, newest release branch |
| macOS | [Martin Riedl's build server](https://ffmpeg.martin-riedl.de) (Apple Silicon and Intel) |

An FFmpeg already on your `PATH` works too, but the app's own copy is recommended.

</details>

<details>
<summary><b>Where your data is stored</b></summary>

Settings, downloaded tools, logs and caches live in a `data` folder next to the executable. If that folder isn't writable, the app uses your per-user app-data folder instead.

</details>

## Contributing

Bug reports, ideas and test results are welcome, especially from **Linux and macOS** users.

- **Found a bug?** [Open an issue](https://github.com/argyris-mpalampanisb/CSharpNLVED/issues/new) with your OS, GPU, driver version and the steps to reproduce it. For export problems, add the log from `data/logs`, but remove any personal file paths first.
- **Sending code?** Keep changes small, put logic in `CSharpNLVED.Core` with tests, and run `dotnet test` before opening a pull request.

## License

CSharpNLVED is free software under the [GNU General Public License v3.0](LICENSE) or later.

## Acknowledgments

Built with [FFmpeg](https://ffmpeg.org/), [Avalonia UI](https://avaloniaui.net/) and [.NET](https://dotnet.microsoft.com/). Thanks to Gyan Doshi, BtbN and Martin Riedl for providing FFmpeg builds.

<sub>CSharpNLVED is an independent project. It is not affiliated with or endorsed by Microsoft or the FFmpeg project. C# and .NET are named only to describe what it is built with.</sub>
