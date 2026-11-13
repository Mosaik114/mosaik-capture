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
  hotkey handling. The engine logs the error to `%LOCALAPPDATA%\MosaikCapture\logs\error.log` and
  restarts itself (but not within its first minute, to avoid a crash loop) with
  `--idle --after <pid>`, so the new process waits for the old one to exit before taking the
  single-instance mutex; the host simply spawns it again on the next capture. Inside the
  engine, both controllers guard their entry points: an unexpected error closes the open
  windows and returns to a consistent state (a running recording is still saved) instead of
  leaving a half-open UI. Anything that still reaches the global handler is classified: recoverable
  I/O, COM, Win32 and timeout errors are logged and the engine continues; anything else is
  treated as a bug, logged, and the engine restarts itself (or, in its first minute, shuts
  down cleanly and is started again by the host on the next capture).
- **Runtime requirements.** .NET Framework 4.8 ships with Windows, so neither process needs
  an installed runtime.

## Inter-process contract

The contract is written down once, in [`contract/contract.json`](../contract/contract.json):
settings keys, defaults and normalisation, default folder names, message numbers, window,
class and mutex names, state bits, exit codes and the test-isolation rules. The host's tests
(`src/contract.rs`) and the engine's (`ContractTests`) both load that file and check their
constants and behaviour against it, so a change on one side fails until the file and the
other side agree. A cross-process test (`engine::tests::quit_protocol_across_processes`)
starts the built engine and drives the quit protocol from the host code. Note that
`WM_APP+1…+4` mean different things on each side: they are addressed to different windows.

| Direction | Mechanism | Meaning |
|---|---|---|
| host → engine | `CreateProcess` `--screenshot` / `--recording` | first capture: start the engine with a command |
| host → engine | `PostMessage` `WM_APP+1` / `+2` to the hidden window titled `MosaikCapture.Engine` | screenshot / toggle recording |
| host → engine | `WM_APP+3` | quit (ignored while the engine is busy, the same state as bit 0 below) |
| host → engine | `SendMessageTimeout` `WM_APP+10` | state query: bit 0 = busy (a recording in any phase from picking the area to saving, the screenshot overlay or editor open, a screenshot being saved, the trim window open or a trim/GIF export running), bit 1 = recording live |
| engine → host | `PostMessage` `WM_APP+20` to window class `MosaikCapture.Host` | microphone / system sound / click display toggled in the recording bar (bits 0–2), so the host persists them |
| anyone → host | second start of the host with `--screenshot`, `--recording`, `--quit`, `--probe`, and a bare `recording` (show settings at the recording section) | forwarded to the running instance (`WM_APP+1…+4`, `+9`, `+10`); see the exit codes below |

Single instance on both sides uses session-local mutexes (`Local\MosaikCapture.Host`,
`Local\MosaikCapture.Engine`). Settings are shared through
`%LOCALAPPDATA%\MosaikCapture\settings.ini` (UTF-16, `[capture]` section): the host owns
writes (temporary file, flushed, then `MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH)`), the
engine only reads. On first start the host copies settings from the pre-1.0 location
(`%LOCALAPPDATA%\Mosaiks-Aufnahme`) and replaces the old autostart value.

The host's exit codes are for scripts and CI; only outcomes a caller acts on have their own
code:

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | the app could not start, or the running instance did not take the forwarded action |
| 3 | self-test failed |
| 5 | `--probe`: no running instance with registered hotkeys (or it did not answer) |
| 11 | `--quit` refused (the engine is busy, see the state query above; a dialog open; or still starting) |

The engine window is only trusted if its process runs `MosaikCapture.Engine.exe` from the
host's own folder (the image path Windows reports, compared case-insensitively), so a
foreign window with the same title is never messaged.

**Test isolation.** With `MOSAIK_CAPTURE_TEST_INSTANCE=<name>` set, both programs append
`.Test.<name>` to every mutex, window class and title, keep their data below
`%TEMP%\MosaikCapture.Test.<name>` instead of `%LOCALAPPDATA%`, and the host never touches
autostart; the host passes the variables on to the engine it starts. Such an instance can
neither reach nor block the real one, which is what the Rust end-to-end and cross-process
tests use. `MOSAIK_CAPTURE_TEST_CONFIG=<ini>` picks the settings file (and alone isolates the
host as instance `preview`), so the old `--preview <ini>` option is gone. Only an isolated
engine accepts the test message that makes it report busy.

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
  The `unsafe` blocks are almost all single Win32 calls; there is no `unsafe` data
  structure or pointer arithmetic beyond reading buffers that Windows hands back.
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
| `Core/` | `Program` (entry point, crash handling), `EngineComposition` (builds the object graph once at start-up), `CaptureBridge` (CLI + IPC), `CaptureSettings` (an immutable snapshot per flow), `ErrorLog`, `NativeMethods` (every P/Invoke and Win32 constant), `UiText` (every German UI string), `ClipboardWriter` (the single clipboard path), `Motion` (shared timing), `Metrics` (shared sizes and shadows), `Palette`, `ToolWindows` |
| `Screenshot/` | `Screenshots` (facade) → `ScreenshotController` → `IScreenshotUi` / `IScreenshotStore`; `ShapeRecognizer` and `TextLineFinder` (pure, tested), `ScreenGrabber`, `SelectionOverlay`, `ScreenshotCard`, `ScreenshotEditor` (+ `.Shapes`, `.Markup`, `.Mouse`, `.Result`) whose state lives in plain, tested models (`EditorDocument` for items, crop, selection, text and undo/redo on model items; `EditorInteraction`, `PenSnap`, `InkStyle`, `CropGeometry`) — the window only draws and routes input, `PinnedScreenshot`, `TextRecognition` |
| `Recording/` | `ScreenRecorder` (facade) → `RecordingController` (state machine) → `IRecordingUi` / `IRecordingBackend` / `IRecordingSession`; `RecordingSession` (behind `IFfmpegRunner` / `IAudioRecorder`, unit-tested with fakes), `Ffmpeg` (every call asynchronous via `RunAsync`, bounded by `FfmpegTimeouts`, cancellable; output read on dedicated threads), `FfmpegArgs`, `TailBuffer`, `AudioRecorder`/`AudioTrack` (WASAPI), `Dxgi`, toolbar, frame, countdown, card, `TrimWindow`, `VideoExport` |
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
                         WASAPI loopback ─► system.f32                    ├─► assemble ─► .mp4
                         WASAPI capture  ─► microphone.f32                ─┘      │
pause/resume: closes the segment, opens a new one on the same clocks              └─► card ─► trim / GIF
```

- **A testable state machine.** `RecordingController` owns the phases
  `Idle → Picking → Counting → Running → Saving` and every failure path (ffmpeg missing,
  monitor not found, ffmpeg dying during the countdown or while running, audio lost, save
  failing). It only talks to three small interfaces, so all transitions are unit-tested with
  fakes; `ScreenRecorder` is a thin static facade for the WPF side.
- **Capture on the GPU.** ffmpeg's `ddagrab` filter uses Desktop Duplication; `Dxgi` maps the
  chosen monitor to the right DXGI output index across all adapters; ddagrab can only capture the primary GPU, so a monitor on another graphics card is reported to the user instead of failing silently. NVENC is probed once per process (off the UI
  thread, 4 s fallback to libx264), and each session fixes its encoder so all segments can be
  joined without re-encoding.
- **Exact start.** ffmpeg is launched shortly *before* the countdown reaches zero, so the
  first frame coincides with the end of the countdown; the countdown, frame and recording bar
  are excluded from capture (`WDA_EXCLUDEFROMCAPTURE`).
- **Audio in sync.** System sound and microphone are recorded by the engine itself (WASAPI
  loopback and capture, `AUTOCONVERTPCM`) onto separate headerless 32-bit float tracks (no WAV size limits, so recordings may run for hours) sharing one clock with the
  video segments. `AudioTrack` pads gaps and trims overlaps by device timestamps; at the end
  the segments are concatenated and the audio is aligned against the segment times measured
  with `ffprobe`, which keeps sound in sync across any number of pauses. If an audio device
  fails, the video is still saved and the card says so.
- **Never block the UI.** Encoder detection and pause/resume are asynchronous; the
  low-level mouse hook for the click display runs on its own thread with its own message
  loop, so Windows never removes it for timing out.
- **Robustness.** If ffmpeg exits on its own (driver reset, locked screen) the recording is
  saved up to that point. ffmpeg runs in a kill-on-close job object so it dies with the
  engine; every step of that setup is checked and logged. Leftovers of crashed recordings
  are cleaned up with the next recording. When saving fails, the raw segments and sound
  tracks move next to the target as `<name> (Rohdaten)` and the failure card offers to
  open them; they are never deleted automatically.
- **Safe tool resolution and quoting.** `ffmpeg`/`ffprobe` are looked up in well-known install
  locations and on `PATH`, accepting only absolute paths on local drives (no `.`, no UNC
  shares). Every command line is built by `FfmpegArgs`, whose quoting is checked against
  Windows' own argument splitter.
- **Trim & GIF.** Exports are frame-accurate re-encodes; GIFs use two passes
  (`palettegen` for exactly the selected frames, then `paletteuse`).

## Testing strategy

| Layer | How it is tested |
|---|---|
| Pure logic (Rust) | `cargo test`: hotkey grammar, settings defaults/normalisation/migration, INI layout and a real round trip through the Win32 profile API on a temp file, CLI parsing, exit codes, forwarding to a fake running instance (incl. timeouts), retrying atomic writes, DPI scaling, COM vtable offsets, the resource writer against golden layouts |
| Host UI (Rust) | `cargo test -- --include-ignored` also creates the real settings window in-process (own window class, temp settings, unusual hotkeys) and drives save, validation, Enter/Escape, IPC and a DPI change; CI always runs it |
| Contract (both) | `contract/contract.json` is loaded by both suites; the Rust side checks the keys and defaults it writes, how it reads numbers (through the real profile API), message numbers, bits, names and exit codes; the C# side checks the keys it reads and their defaults, number parsing, normalisation, names and messages. `cargo test -- --include-ignored` also starts the built engine (`bin\`) `--idle` under test-only names and checks that it reports idle and quits, and that it refuses to quit while (test-only) busy |
| Engine logic (C#) | xUnit on a shared STA dispatcher thread: the recording state machine with fakes, WASAPI track padding/trimming with a fake capture client, shape recognition, highlighter snapping, selection geometry (including a randomized check over 5,000 rectangles), command-line quoting (500 random round trips through `CommandLineToArgvW`), settings parsing, error-log rotation |
| Engine + ffmpeg | real ffmpeg runs: audio stays in sync after a pause, one or zero audio tracks, trim with and without sound, GIF export, broken input files (skipped when ffmpeg is missing) |
| Visual | pixel tests render the states of the real windows off-screen (editor toolbar, menu, scene; recording bar, card, frame, countdown; trim window) and compare them with reference images; on mismatch the actual image and a red diff are written. They skip, naming the font, when a reference font is missing; `MOSAIK_PIXEL_TOLERANCE=lenient` compares slightly blurred images to absorb font-version differences (used in CI) |

All suites run on every push. Only the settings-window end-to-end test and the
cross-process test create real windows (briefly, under test-only names with temp
settings); nothing touches the real settings file, data folder or autostart entry.

**Not covered automatically:** live screen capture itself (Desktop Duplication needs a real,
unlocked interactive desktop, which neither CI nor a hidden test desktop provides), real
audio devices, mouse interaction on the selection overlay, and multi-monitor DPI changes
on physical displays. These are covered by `--live-test` and manual checks before a release.

## Three problems worth describing

**Keeping audio in sync across pauses.** Pausing has to stop the video pipeline, and
restarting a capture input never happens at an exact instant, so every pause risks a few
milliseconds of offset that add up over a long recording. The engine therefore keeps audio
out of ffmpeg: it records system sound and microphone itself via WASAPI onto continuous
tracks positioned by the device's own timestamps (`AudioTrack` pads gaps and trims overlaps).
Each video segment records its start on both the wall clock and the audio clock; when
saving, the real segment durations are measured with `ffprobe` and the audio is cut to
exactly those windows. A real-ffmpeg test pauses, resumes and asserts the offset
([`SystemSoundAndMicrophoneStayInSyncAfterAPause`](../tests/MosaikCapture.Engine.Tests/Recording/FfmpegIntegrationTests.cs)).

**No black flash on very large screens.** A transparent, layered WPF window showing the
frozen screen is too slow at 5120 × 1440 and can show a black frame before WPF has rendered.
The overlay is instead an opaque window that is created *cloaked* through DWM, renders its
first frame while invisible and is uncloaked only then; only the dimming animates.

**Joining segments that use different encoders.** Encoder detection (NVENC or x264) used to
run lazily with a timeout on the UI thread. If it timed out, the first segment used x264 and
later ones NVENC, and the stream-copy concatenation produced a broken file. Detection now runs
once in the background, and each recording session fixes its encoder for all its segments
([`RecordingSessionTests`](../tests/MosaikCapture.Engine.Tests/Recording/RecordingSessionTests.cs)).
