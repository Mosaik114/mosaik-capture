# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-12

First public release as a standalone project.

### Added
- Settings window, icons and tray icon follow each monitor's scaling (per-monitor DPI).
- Modern folder picker that also accepts long paths.
- Keyboard support: focus and pressed states on buttons, Enter saves, Escape hides; the
  tray icon works with keyboard and pen.
- Messages when something goes wrong instead of silent failures: recording saved without
  sound, text recognition failed, copying to the clipboard failed, ffmpeg not responding.
- Closing the trim window cancels a running export.

### Changed
- Executables are now `MosaikCapture.exe` and `MosaikCapture.Engine.exe`. Settings moved to
  `%LOCALAPPDATA%\MosaikCapture` and are migrated automatically; the autostart entry is
  renamed and the old one removed; logs live in `%LOCALAPPDATA%\MosaikCapture\logs`.
- Custom shortcuts must include Ctrl or Alt, so they can't swallow normal typing.

### Fixed
- Pausing or resuming a recording no longer freezes the UI; the click display can no longer
  be dropped by Windows for responding too slowly.
- All parts of a paused recording use the same encoder, so joining them can't produce a
  broken video; a hanging ffmpeg can no longer leave the engine busy forever.
- Switching the default audio device during a recording (e.g. plugging in headphones) is
  followed within about a second instead of recording silence.
- Two quick trims or GIF exports can no longer overwrite each other.
- Recordings longer than about 93 minutes keep their sound (audio tracks no longer use
  size-limited WAV files).
- A screenshot on a locked or secure desktop shows a message instead of a black image; a
  monitor on a second graphics card is reported as not recordable instead of failing.
- Double-clicking a button in the settings window no longer runs its action twice.
- Unsaved edits in the settings window are kept when it is opened again.
- After a crash the engine restarts reliably (it waits for the old process first).
- `--quit` and forwarded commands report failures through their exit codes
  (see [docs/architecture.md](docs/architecture.md#inter-process-contract)).

### Internal
- Rust host rewritten from a `no_std` single file into an idiomatic Cargo project
  (`windows-sys`, `clippy::pedantic`, documented `unsafe`), 117 unit tests and a
  window end-to-end test.
- C# engine moved to an SDK-style project with nullable types and analyzers; recording and
  screenshot flows extracted into tested state machines; one home each for P/Invoke, UI
  strings, metrics and timing; 443 tests including real ffmpeg runs and pixel comparisons.
- `build.ps1`, GitHub Actions CI with packaging and release upload, secret scanning,
  Dependabot.

## 0.1.0 - 2026-11-08

- First standalone version (before this repository existed): screenshot and
  screen-recording tray app with its own hotkeys (Shift+Alt+4 / Shift+Alt+5), settings
  window and autostart. Its sources were imported unchanged as the first commit of this
  repository on 2026-11-12 (tag [`original-import`](https://github.com/Mosaik114/mosaik-capture/tree/original-import)),
  so the whole 1.0.0 restructuring can be read as a before/after diff.

[1.0.0]: https://github.com/Mosaik114/mosaik-capture/releases/tag/v1.0.0
