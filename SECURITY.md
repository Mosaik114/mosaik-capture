# Security policy

## Reporting a vulnerability

Please report security issues privately through
[GitHub's private vulnerability reporting](https://github.com/Mosaik114/mosaik-capture/security/advisories/new)
instead of opening a public issue. You can expect a first answer within a week.

## Design notes relevant to security

- **No network access.** Neither process opens a socket. Text recognition uses the
  on-device Windows OCR engine.
- **No elevation.** Both executables run with the user's normal rights
  (`asInvoker` manifest); autostart is a per-user `HKCU\…\Run` value.
- **ffmpeg is resolved, never downloaded.** The engine only runs `ffmpeg.exe` /
  `ffprobe.exe` from an absolute path on a local drive; relative paths and network
  shares (UNC) are rejected, so a planted binary in the working directory or on a
  share cannot be picked up.
- **IPC stays local to the session.** The two processes talk through window messages
  and session-local mutexes (`Local\…`); no named pipes or files are used as a channel.
  Only processes of the same user in the same session can send these messages, nothing
  privileged can be triggered (at worst a capture overlay opens or an idle engine quits),
  capture commands are rate-limited to 10 per second, and the host only messages an engine
  whose process image is its own `MosaikCapture.Engine.exe`.
- **Settings are written atomically** (temporary file + `MoveFileEx`), so a crash cannot
  leave a half-written configuration.
- **Supply chain.** Rust and NuGet dependencies are locked (`Cargo.lock`,
  `packages.lock.json`), GitHub Actions are pinned to commit SHAs, Dependabot proposes
  updates, and every push is scanned for committed secrets.
