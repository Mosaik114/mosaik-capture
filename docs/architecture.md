# Architecture

Mosaik Capture is two cooperating Windows processes:

| Process | Language | Lifetime | Responsibility |
|---|---|---|---|
| `Mosaiks-Aufnahme.exe` (*host*) | Rust, `windows-sys` | from login, always | global hotkeys, settings window, tray icon, autostart, starting the engine |
| `Mosaiks-Capture-Engine.exe` (*engine*) | C# 14, WPF, .NET Framework 4.8 | from the first capture, then resident | freeze-frame overlay, editor, OCR, pinning, screen recording, trimming, GIF export |

## Why split it this way

- **Cost of being always on.** The host runs all day. As a native Win32 app it is a few
  hundred kilobytes, starts in milliseconds and spends its life blocked in `GetMessageW`.
  A WPF process idling in the background would cost tens of megabytes for nothing.
- **Productivity where the UI is.** The editor needs vector shapes, effects (blur,
  drop shadows), hit testing, undo/redo, animations and per-monitor DPI. WPF provides all
  of that; doing it in raw Win32/GDI from Rust would multiply the code for no user benefit.
- **Fault isolation.** A crash in the editor or in an ffmpeg pipeline cannot take down
  hotkey handling. The engine logs the error to `Fehler.log` and restarts itself (but not
  within its first minute, to avoid a crash loop); the host simply spawns it again on the
  next capture.
- **Runtime requirements.** .NET Framework 4.8 ships with Windows, so neither process needs
  an installed runtime.

## Inter-process contract

Both sides keep the contract in one place (`src/ipc.rs`, `engine/Core/CaptureBridge.cs`)
and pin it with tests, so a change on one side fails the other side's suite.

| Direction | Mechanism | Meaning |
|---|---|---|
| host → engine | `CreateProcess` `--screenshot` / `--recording` | first capture: start the engine with a command |
| host → engine | `PostMessage` `WM_APP+1` / `+2` to the hidden window *Mosaiks Capture Engine* | screenshot / toggle recording |
| host → engine | `WM_APP+3` | quit (refused while a recording is running) |
| host → engine | `SendMessageTimeout` `WM_APP+10` | state query: bit 0 = busy (selection/countdown/recording), bit 1 = recording live |
| engine → host | `PostMessage` `WM_APP+20` to class `MosaiksAufnahmeRust` | microphone / system sound / click display toggled in the recording bar (bits 0–2), so the host persists them |
| anyone → host | second start of the host with `--screenshot`, `--recording`, `--quit`, `--probe`, `recording` | forwarded to the running instance (`WM_APP+1…+4`, `+9`, `+10`) |

Single instance on both sides uses session-local mutexes (`Local\MosaiksCaptureRust`,
`Local\MosaiksCaptureEngine`). Settings are shared through
`%LOCALAPPDATA%\Mosaiks-Aufnahme\settings.ini` (UTF-16, `[capture]` section): the host owns
writes (temporary file + `MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH)`), the engine only
reads.

## Host internals (Rust)

```
main.rs ──► cli.rs (Command) ──► single_instance.rs ──► forward to running host
   │
   └──► ui/ (settings window + tray) ── app.rs holds all state
              │            │
              │            ├── store.rs / config.rs   settings model + atomic INI I/O
              │            ├── autostart.rs           HKCU\…\Run value
              │            └── hotkey.rs              parse + RegisterHotKey(MOD_NOREPEAT)
              └── engine.rs / ipc.rs                  find, query, start, message the engine
```

- **No global mutable state.** The single `App` struct lives behind the window's
  `GWLP_USERDATA`; interior mutability (`Cell`/`RefCell`) is never borrowed across a
  Win32 call that could re-enter the window procedure.
- **`unsafe` is fenced in.** FFI calls sit in thin wrappers, each `unsafe` block carries a
  `// SAFETY:` justification (enforced by `clippy::undocumented_unsafe_blocks`), and all
  logic — hotkey parsing, settings normalisation, INI layout, argument parsing — is pure
  Rust that the unit tests reach without a window.
- **Saving is transactional.** New hotkeys are registered before anything is written; if
  registration, autostart or the file write fails, the previous hotkeys and autostart are
  restored and the user sees why.
- **Resources without tools.** `build.rs` writes the `.rsrc` COFF section (icon group,
  manifest, version info) itself, so the GNU and MSVC toolchains produce byte-identical
  resources without `rc.exe` or `windres`.

## Engine internals (C#)

### Screenshot

1. **Freeze.** All monitors are captured into one bitmap before anything is shown, so
   open menus and tooltips are preserved.
2. **Overlay.** A full-virtual-screen window shows the frozen image. It is created
   *cloaked* (`DWMWA_CLOAK`) and only uncloaked once WPF has rendered — at 5120 × 1440 a
   transparent layered window would be too slow and would flash black. Only the dimming
   fades in. A magnifier shows the pixels under the cursor; clicking without dragging picks
   the window under the cursor (`EnumWindows` + DWM extended frame bounds).
3. **Deliver.** On release the PNG is saved and put on the clipboard, and a
   card offers *edit*, *pin*, *text*, *folder*, *delete*; the card can be dragged into other
   apps.
4. **Editor.** Tools are split by concern into partial classes: `ShotEditor.Shapes`
   (recognising rough pen strokes as line, arrow, ellipse, rectangle, triangle or rhombus),
   `.Markup` (highlighter that finds the text line under the stroke by separating ink from the
   dominant background colour and snaps to its height and word boundaries; plates, numbers,
   pixelate, blur, spotlight), `.Mouse` (interaction) and `.Result` (flattening and export).
5. **OCR.** `Windows.Media.Ocr` via the WinRT projection (`Microsoft.Windows.SDK.Contracts`);
   images are scaled to the engine's optimal size first. Fully offline.

### Recording

```
selection ─► countdown ─► ffmpeg (ddagrab → NVENC|x264 → .mkv segment)   ─┐
                         WASAPI loopback ─► system.wav                    ├─► assemble ─► .mp4
                         WASAPI capture  ─► mic.wav                       ─┘      │
pause/resume: closes the segment, opens a new one on the same clocks              └─► card ─► trim / GIF
```

- **Capture on the GPU.** ffmpeg's `ddagrab` filter uses Desktop Duplication; `Dxgi.cs`
  maps the chosen monitor to the right DXGI output index. Encoding uses NVENC when a probe
  succeeds, otherwise libx264.
- **Exact start.** ffmpeg is launched shortly *before* the countdown reaches zero, so the
  first frame coincides with the end of the countdown; the countdown, frame and recording bar
  are excluded from capture (`WDA_EXCLUDEFROMCAPTURE`).
- **Audio in sync.** System sound and microphone are recorded by the engine itself (WASAPI
  loopback and capture, `AUTOCONVERTPCM`) onto separate WAV tracks sharing one clock with the
  video segments. Each segment stores its start on both clocks; at the end the segments are
  concatenated and the audio is cut and aligned against the measured segment times
  (`ffprobe`), which keeps sound in sync across any number of pauses.
- **Robustness.** If ffmpeg exits on its own (driver reset, locked screen) the recording is
  saved up to that point. ffmpeg runs inside a job object so it dies with the engine.
  Leftovers of aborted recordings are cleaned up on the next start.
- **Safe tool resolution.** `ffmpeg`/`ffprobe` are looked up in well-known install
  locations and on `PATH`, accepting only absolute paths on local drives (no `.`, no UNC
  shares).
- **Trim & GIF.** Exports are frame-accurate re-encodes; GIFs use two passes
  (`palettegen` for exactly the selected frames, then `paletteuse`).

## Testing strategy

| Layer | How it is tested |
|---|---|
| Pure logic (Rust) | `cargo test`: hotkey grammar, settings defaults/normalisation, INI layout and a real round trip through the Win32 profile API on a temp file, CLI parsing, IPC bit layouts |
| Host UI (Rust) | an ignored-by-default end-to-end test creates the real settings window in-process (with unusual hotkeys so it never collides with a running instance) and drives save, validation and IPC |
| Engine logic (C#) | xUnit on a shared STA dispatcher thread: shape recognition, highlighter snapping, `Rec.Fit` geometry (including a randomized check over 5,000 rectangles), ffmpeg argument construction, file naming, settings parsing, error-log rotation |
| Engine + ffmpeg | real ffmpeg runs: audio stays in sync after a pause, one or zero audio tracks, trim with and without sound, GIF export, broken input files (skipped when ffmpeg is missing) |
| Visual | 13 windows (editor toolbar, menu, scene, recording bar/card/frame, countdown, trim window) are rendered off-screen and compared pixel by pixel with references; on mismatch the actual image and a red diff are written. Opt-in (`Category=Pixel`) because fonts differ on CI runners |

Nothing in the suites opens a visible window or touches the real settings file or
autostart entry.
