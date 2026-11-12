# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-12

First public release as a standalone project.

### Added
- Cargo project for the Rust tray app with `windows-sys`, split into modules for CLI,
  single instance, hotkeys, settings, autostart, IPC and UI; 94 unit tests and an
  end-to-end test that drives the real settings window (runs in CI).
- Per-monitor DPI support, `IFileOpenDialog` folder picker with long paths, keyboard focus
  and pressed states on buttons, a sharp DPI-aware tray icon.
- `build.rs` with a tested resource writer that embeds icon, manifest and version info
  without external tools.
- SDK-style C# project and solution for the capture engine with central package management,
  lock files, nullable reference types and .NET analyzers.
- `RecordingController` state machine behind small interfaces, `FfmpegArgs` command-line
  builder, `NativeMethods` and `UiText` as single homes for interop and UI strings.
- xUnit test project with 251 cases, including the recording state machine, WASAPI track
  logic, real ffmpeg runs and pixel comparisons of 13 UI states.
- `build.ps1`, GitHub Actions CI (fmt, clippy, tests, format, packaging, release upload),
  secret scanning, Dependabot; English README, architecture notes, security policy,
  MIT license.

### Changed
- Executables renamed to `MosaikCapture.exe` and `MosaikCapture.Engine.exe`; settings moved
  to `%LOCALAPPDATA%\MosaikCapture` (migrated automatically), autostart value renamed
  (old value removed), engine log renamed to `error.log`.
- Rust host rewritten from a `no_std` single-file program with hand-written FFI and global
  mutable state into idiomatic std Rust; engine sources reorganised and renamed with English
  identifiers and comments.

### Fixed
- Pausing or resuming a recording and the first start no longer block the UI thread; the
  click-display mouse hook runs on its own thread.
- All segments of a recording use the same encoder, so joining them never mixes NVENC and
  x264 streams.
- ffmpeg exiting immediately is detected (exit handler attached before start).
- Process, GDI and native-memory handles are released; failures of the ffmpeg job object
  are logged instead of ignored.
- A failed audio device no longer silently drops sound: the video is saved and the card says
  "Aufnahme ohne Ton gespeichert"; failed text recognition is reported separately from
  "no text found".
- Escape with an open drop-down list closes only the list; forwarding to an unresponsive
  running instance reports exit code 9 instead of success.

## 0.1.0 - 2026-11-08

- First standalone version (before this repository existed): screenshot and
  screen-recording tray app with its own hotkeys (Shift+Alt+4 / Shift+Alt+5), settings
  window and autostart. Its sources were imported unchanged as the first commit of this
  repository on 2026-11-12.

[1.0.0]: https://github.com/Mosaik114/mosaik-capture/releases/tag/v1.0.0
