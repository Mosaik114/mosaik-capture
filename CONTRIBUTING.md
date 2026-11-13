# Contributing

Thanks for taking an interest. Bug reports, ideas and pull requests are welcome.

## Before you start

- For anything bigger than a small fix, open an issue first so we can agree on the approach.
- The UI is German on purpose (see the README); please keep new UI texts in
  `engine/Core/UiText.cs` (C#) or `src/ui/strings.rs` (Rust) so they stay in one place.

## Build and test

Prerequisites: Rust (stable) and the .NET SDK 10. On Windows:

```powershell
./build.ps1 -Test
```

This builds both programs into `bin\` and runs the Rust and C# test suites, including the
pixel comparisons. Two Rust tests are opt-in locally: the end-to-end test creates a real
(short-lived) settings window, and the cross-process test starts the engine from `bin\`
(build it first). Both run under test-only names (`MOSAIK_CAPTURE_TEST_INSTANCE`), so they
never meet an installed copy:

```powershell
cargo test -- --include-ignored
```

The host–engine contract (settings keys and defaults, message numbers, names, exit codes)
lives in `contract/contract.json`. Change it together with both sides; each test suite checks
its side against the file.

## What CI expects

Every pull request must pass the same checks as `main`:

- `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings` (pedantic lints are on;
  every `unsafe` block needs a `// SAFETY:` comment), `cargo test -- --include-ignored`
- `dotnet format --verify-no-changes`, a warning-free Release build, `dotnet test`
- locked dependencies: update `Cargo.lock` / `packages.lock.json` together with any
  dependency change

Please add or adjust tests for behaviour you change, and describe user-visible changes in
`CHANGELOG.md` under an *Unreleased* heading.

## Reporting security issues

Please don't open a public issue — see [SECURITY.md](SECURITY.md).
