# Architecture

Mosaik Capture is two cooperating Windows processes:

| Process | Language | Lifetime | Responsibility |
|---|---|---|---|
| `MosaikCapture.exe` (*host*) | Rust, `windows-sys` | from login, always | global hotkeys, settings window, tray icon, autostart, starting the engine |
| `MosaikCapture.Engine.exe` (*engine*) | C# 14, WPF, .NET Framework 4.8 | from the first capture, then resident | freeze-frame overlay, editor, OCR, pinning, screen recording, trimming, GIF export |

## Why split it this way

- **Cost of being always on.** The host runs all day. As a native Win32 app it is a few
  hundred kilobytes, starts in milliseconds and spends its life blocked in `GetMessageW`.
  A WPF process idling in the background would cost tens of megabytes for nothing.
- **Productivity where the UI is.** The editor needs vector shapes, effects (blur,
  drop shadows), hit testing, undo/redo, animations and per-monitor DPI. WPF provides all
  of that; doing it in raw Win32/GDI from Rust would multiply the code for no user benefit.
- **Fault isolation.** A crash in the editor or in an ffmpeg pipeline cannot take down
  hotkey handling. The engine logs the error to `error.log` next to the executable and
  restarts itself (but not within its first minute, to avoid a crash loop); the host simply
  spawns it again on the next capture.
- **Runtime requirements.** .NET Framework 4.8 ships with Windows, so neither process needs
  an installed runtime.

## Inter-process contract

Both sides keep the contract in one place (`src/ipc.rs`, `engine/Core/CaptureBridge.cs`)
and pin it with tests, so a change on one side fails that side's suite.

| Direction | Mechanism | Meaning |
|---|---|---|
| host → engine | `CreateProcess` `--screenshot` / `--recording` | first capture: start the engine with a command |
| host → engine | `PostMessage` `WM_APP+1` / `+2` to the hidden window titled `MosaikCapture.Engine` | screenshot / toggle recording |
| host → engine | `WM_APP+3` | quit (refused while a recording is running) |
| host → engine | `SendMessageTimeout` `WM_APP+10` | state query: bit 0 = busy (selection/countdown/recording), bit 1 = recording live |
| engine → host | `PostMessage` `WM_APP+20` to window class `MosaikCapture.Host` | microphone / system sound / click display toggled in the recording bar (bits 0–2), so the host persists them |
| anyone → host | second start of the host with `--screenshot`, `--recording`, `--quit`, `--probe`, `recording` | forwarded to the running instance (`WM_APP+1…+4`, `+9`, `+10`); exit code 9 if it does not answer |

Single instance on both sides uses session-local mutexes (`Local\MosaikCapture.Host`,
`Local\MosaikCapture.Engine`). Settings are shared through
`%LOCALAPPDATA%\MosaikCapture\settings.ini` (UTF-16, `[capture]` section): the host owns
writes (temporary file, flushed, then `MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH)`), the
engine only reads. On first start the host copies settings from the pre-1.0 location
(`%LOCALAPPDATA%\Mosaiks-Aufnahme`) and replaces the old autostart value.

## Host internals (Rust)

```
main.rs ──► cli.rs (Command) ──► single_instance.rs ──► forward to running host
   │
   └──► ui/ (settings window + tray) ── app.rs holds all state
              │            │
              │            ├── store.rs / config.rs   settings model + atomic INI I/O + migration
              │            ├── autostart.rs           HKCU\…\Run value
              │            └── hotkey.rs              parse + RegisterHotKey(MOD_NOREPEAT)
              └── engine.rs / ipc.rs                  find, query, start, message the engine
```

- **No global mutable state.** The single `App` struct lives behind the window's
  `GWLP_USERDATA`; interior mutability (`Cell`/`RefCell`) is never borrowed across a
  Win32 call that could re-enter the window procedure.
- **`unsafe` is fenced in.** FFI calls sit in thin wrappers, each `unsafe` block carries a
  `// SAFETY:` justification (enforced by `clippy::undocumented_unsafe_blocks`), and all
  logic — hotkey parsing, settings normalisation, INI layout, migration, argument parsing,
  DPI scaling — is pure Rust that the unit tests reach without a window.
- **Per-monitor DPI.** Layout is a table of controls in 96-DPI units; on `WM_DPICHANGED`
  fonts are rebuilt and every child is moved to the scaled rectangle.
- **Saving is transactional.** New hotkeys are registered before anything is written; if
  registration, autostart or the file write fails, the previous hotkeys and autostart are
  restored and the user sees why.
- **Modern shell APIs.** `IFileOpenDialog` (folder mode, long paths) and
  `SHGetKnownFolderPath`, with minimal hand-declared COM vtables whose slot offsets are tested.
- **Resources without tools.** `build.rs` uses `build/rsrc.rs` to write the `.rsrc` COFF
  section (icon group, manifest, version info) itself, so the GNU and MSVC toolchains
  produce identical resources without `rc.exe` or `windres`. The writer is unit-tested
  against golden layouts.

## Engine internals (C#)

| Folder | Contents |
|---|---|
| `Core/` | `Program` (entry point, crash handling), `CaptureBridge` (CLI + IPC), `CaptureSettings`, `ErrorLog`, `NativeMethods` (every P/Invoke and Win32 constant), `UiText` (every German UI string), `Palette`, `ToolWindows` |
| `Screenshot/` | `Screenshots` (facade), `ScreenGrabber`, `SelectionOverlay`, `ScreenshotCard`, `ScreenshotEditor` (+ `.Shapes`, `.Markup`, `.Mouse`, `.Result`), `PinnedScreenshot`, `TextRecognition` |
| `Recording/` | `ScreenRecorder` (facade) → `RecordingController` (state machine) → `IRecordingUi` / `IRecordingBackend` / `IRecordingSession`; `RecordingSession`, `Ffmpeg`, `FfmpegArgs`, `AudioRecorder`/`AudioTrack` (WASAPI), `Dxgi`, toolbar, frame, countdown, card, `TrimWindow`, `VideoExport` |
| `Diagnostics/` | `--self-test` and `--live-test` |

### Screenshot

1. **Freeze.** All monitors are captured into one bitmap before anything is shown, so
   open menus and tooltips are preserved.
2. **Overlay.** A full-virtual-screen window shows the frozen image. It is created
   *cloaked* (`DWMWA_CLOAK`) and only uncloaked once WPF has rendered — at 5120 × 1440 a
   transparent layered window would be too slow and would flash black. Only the dimming
   fades in. A magnifier shows the pixels under the cursor; clicking without dragging picks
   the window under the cursor (`EnumWindows` + `DWMWA_EXTENDED_FRAME_BOUNDS`).
3. **Deliver.** On release the PNG is saved and put on the clipboard, and a card offers
   *edit*, *pin*, *text*, *folder*, *delete*; the card can be dragged into other apps.
4. **Editor.** Tools are split by concern into partial classes: `.Shapes` (recognising rough
   pen strokes as line, arrow, ellipse, rectangle, triangle or rhombus), `.Markup`
   (a highlighter that finds the text line under the stroke by separating ink from the
   dominant background colour and snaps to its height and word boundaries; plates, numbers,
   pixelate, blur, spotlight), `.Mouse` (interaction) and `.Result` (flattening and export).
5. **OCR.** `Windows.Media.Ocr` via the WinRT projection (`Microsoft.Windows.SDK.Contracts`);
   images are scaled to the engine's optimal size first. Fully offline; "no text found" and
   "recognition failed" are reported separately.

### Recording

```
selection ─► countdown ─► ffmpeg (ddagrab → NVENC|x264 → segment-NN.mkv) ─┐
                         WASAPI loopback ─► system.wav                    ├─► assemble ─► .mp4
                         WASAPI capture  ─► mic.wav                       ─┘      │
pause/resume: closes the segment, opens a new one on the same clocks              └─► card ─► trim / GIF
```

- **A testable state machine.** `RecordingController` owns the phases
  `Idle → Picking → Counting → Running → Saving` and every failure path (ffmpeg missing,
  monitor not found, ffmpeg dying during the countdown or while running, audio lost, save
  failing). It only talks to three small interfaces, so all transitions are unit-tested with
  fakes; `ScreenRecorder` is a thin static facade for the WPF side.
- **Capture on the GPU.** ffmpeg's `ddagrab` filter uses Desktop Duplication; `Dxgi` maps the
  chosen monitor to the right DXGI output index. NVENC is probed once per process (off the UI
  thread, 4 s fallback to libx264), and each session fixes its encoder so all segments can be
  joined without re-encoding.
- **Exact start.** ffmpeg is launched shortly *before* the countdown reaches zero, so the
  first frame coincides with the end of the countdown; the countdown, frame and recording bar
  are excluded from capture (`WDA_EXCLUDEFROMCAPTURE`).
- **Audio in sync.** System sound and microphone are recorded by the engine itself (WASAPI
  loopback and capture, `AUTOCONVERTPCM`) onto separate WAV tracks sharing one clock with the
  video segments. `AudioTrack` pads gaps and trims overlaps by device timestamps; at the end
  the segments are concatenated and the audio is aligned against the segment times measured
  with `ffprobe`, which keeps sound in sync across any number of pauses. If an audio device
  fails, the video is still saved and the card says so.
- **Never block the UI.** Encoder detection and pause/resume are asynchronous; the
  low-level mouse hook for the click display runs on its own thread with its own message
  loop, so Windows never removes it for timing out.
- **Robustness.** If ffmpeg exits on its own (driver reset, locked screen) the recording is
  saved up to that point. ffmpeg runs in a kill-on-close job object so it dies with the
  engine; every step of that setup is checked and logged. Leftovers of aborted recordings
  are cleaned up on the next start.
- **Safe tool resolution and quoting.** `ffmpeg`/`ffprobe` are looked up in well-known install
  locations and on `PATH`, accepting only absolute paths on local drives (no `.`, no UNC
  shares). Every command line is built by `FfmpegArgs`, whose quoting is checked against
  Windows' own argument splitter.
- **Trim & GIF.** Exports are frame-accurate re-encodes; GIFs use two passes
  (`palettegen` for exactly the selected frames, then `paletteuse`).

## Testing strategy

| Layer | How it is tested |
|---|---|
| Pure logic (Rust) | `cargo test`, 82 tests: hotkey grammar, settings defaults/normalisation/migration, INI layout and a real round trip through the Win32 profile API on a temp file, CLI parsing, exit codes, DPI scaling, COM vtable offsets, the resource writer against golden layouts |
| Host UI (Rust) | `cargo test -- --ignored` creates the real settings window in-process (own window class, temp settings, unusual hotkeys) and drives save, validation, Enter/Escape, IPC and a DPI change; runs in CI |
| Engine logic (C#) | xUnit on a shared STA dispatcher thread: the recording state machine with fakes, WASAPI track padding/trimming with a fake capture client, shape recognition, highlighter snapping, selection geometry (including a randomized check over 5,000 rectangles), command-line quoting (500 random round trips through `CommandLineToArgvW`), settings parsing, error-log rotation |
| Engine + ffmpeg | real ffmpeg runs: audio stays in sync after a pause, one or zero audio tracks, trim with and without sound, GIF export, broken input files (skipped when ffmpeg is missing) |
| Visual | 5 pixel tests render 13 states of the real windows off-screen (editor toolbar, menu, scene; recording bar, card, frame, countdown; trim window) and compare them with reference images; on mismatch the actual image and a red diff are written. They skip, naming the font, when a reference font is missing; `MOSAIK_PIXEL_TOLERANCE=lenient` compares slightly blurred images to absorb font-version differences (used in CI) |

All suites run on every push; nothing in them opens a visible window or touches the real
settings file or autostart entry.
