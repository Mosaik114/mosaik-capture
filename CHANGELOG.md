# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-12

First public release as a standalone project.

### Added
- Cargo project for the Rust tray app with `windows-sys`, split into modules for hotkeys,
  settings, autostart, IPC and UI; 60 unit tests and an end-to-end test of the settings window.
- `build.rs` that embeds icon, manifest and version info without external resource tools.
- SDK-style C# project and solution for the capture engine, with central package management,
  lock files, nullable reference types and .NET analyzers.
- xUnit test project with 167 cases, including real ffmpeg runs and pixel comparisons of
  13 UI renders.
- `build.ps1`, GitHub Actions CI (fmt, clippy, tests, format, packaging, release upload),
  secret scanning, Dependabot.
- English README, architecture notes, security policy, MIT license.

### Changed
- Rust host rewritten from a `no_std` single-file program with hand-written FFI and
  global mutable state into idiomatic std Rust; behaviour, command line, IPC and settings
  format are unchanged.
- Engine sources reorganised into `Core`, `Screenshot`, `Recording` and `Diagnostics`,
  comments translated to English; behaviour and IPC are unchanged.

### Removed
- The old Python build and QA scripts (replaced by `build.ps1`, the Rust end-to-end test
  and the C# test project).

## [0.1.0] - 2026-11-08

- Split the screenshot and screen-recording features out of the private *Mosaiks-Suche*
  desktop app into a standalone tray app with its own hotkeys (Shift+Alt+4 / Shift+Alt+5),
  settings window and autostart.

[1.0.0]: https://github.com/Mosaik114/mosaik-capture/releases/tag/v1.0.0
[0.1.0]: https://github.com/Mosaik114/mosaik-capture/commit/4a96d5c
