# Contributing to Dynamite TTS

Thanks for helping. Bug reports, fixes, better tone presets, documentation and new features are all welcome. This guide covers how the project is built and checked, and the few rules that keep it working on three operating systems at once.

## Reporting a bug

Open an [issue](https://github.com/robrab2000/dynamite-tts/issues) with:

- your system (Windows / macOS / Linux, version, and on Linux the desktop and Wayland or X11),
- the Dynamite TTS version (`dynamite-tts --version`, or under the logo in Settings),
- what you did, what you expected, and what happened,
- the relevant part of the log. Settings → General → **Open folder** takes you to it; the README lists where it lives. The log records timings and phases only, never the text that was read, so it's safe to share.

## Getting set up

You need [Rust](https://rustup.rs) 1.89 or newer (stable). Then:

| System | Also needed |
|---|---|
| Windows | The MSVC build tools (Visual Studio Build Tools, "Desktop development with C++"). [Inno Setup 6](https://jrsoftware.org/isinfo.php) only if you build the installer. |
| macOS | Xcode or the Command Line Tools. Apple Silicon only. |
| Linux | `pkg-config libgtk-3-dev libasound2-dev` (Ubuntu names). AppIndicator, wl-clipboard and xclip are runtime dependencies only. |

```bash
git clone https://github.com/robrab2000/dynamite-tts
cd dynamite-tts
cargo run --release          # debug builds work, but Phonon is much slower in them
```

The first time it speaks, the app downloads the Phonon model (65 MB) into your data folder. To start fresh, quit the app and delete the settings file and data folder listed in the README.

## Before you open a pull request

Run what CI runs:

```bash
cargo fmt
cargo clippy --all-targets -- -D warnings
cargo test
```

CI runs clippy and the tests on Windows, macOS and Linux for every pull request, and all three must pass.

Much of the code only exists on one or two systems (`#[cfg(windows)]`, `#[cfg(target_os = "macos")]`, `#[cfg(target_os = "linux")]`). Code you can't compile locally is checked only by CI, so watch its results. The usual surprise is code or an import that is used everywhere except one system: clippy treats it as dead code there. Gate it with the same `cfg` as the code that uses it.

### Visual and timing checks

Some checks render images instead of needing a screen, which helps when you change the UI or the icons:

```bash
cargo test -- --ignored                        # Settings pages, status pills, EQ benchmark
cargo test --release eq_cost -- --ignored --nocapture
cargo run --release --example make_icons       # regenerate every icon from assets/logo.png
```

They write to `target/settings-snapshots`, `target/status-snapshots` and `target/icon-preview`. If your change affects how something looks, include before-and-after images in the pull request. If it affects the README's images, regenerate them and copy them into `docs/images`.

Things the tests can't cover, please try by hand on the systems you have: the shortcuts, reading the highlight from a couple of apps, the status pill and tray icon, and Settings.

## How the code is organised

The README's "Source layout" section maps the files. A few ideas run through all of it:

- **One process for the tray, another for Settings.** The tray app (`app.rs`) owns the shortcuts, the speech pipeline and the status pill. Settings (`settings_ui.rs`) runs as `dynamite-tts --settings-window`, saves `settings.json` as you change things and tells the app to `reload` over the control socket (`control.rs`). New settings go in `settings.rs`, with defaults, normalization and loading, and a test.
- **Speech never blocks the UI thread.** Each request runs on a worker thread and reports back with events tagged by a generation number (`speech.rs`). A newer request or a stop makes older work stop and its late events harmless.
- **Phonon runs in-process** through the `ptts` crate (`phonon.rs`). Its model and voices download from pinned Hugging Face revisions, and the model files are checked against SHA-256 hashes. Keep it that way if you change versions: update the revision, sizes and hashes together, in `phonon.rs` and in the packaging scripts that bundle the model.
- **The audio callback must stay cheap.** `audio.rs` plays from a queue in the device callback, which also runs the EQ (`eq.rs`). Don't allocate, lock for long, or do I/O there.

## Rules that matter

- **Never log or store what was read.** No highlighted text, summaries or explanations in the log, settings or anywhere on disk. Log lengths and timings instead.
- **Nothing leaves the machine** except the pinned model and voice downloads and requests to the user's own Lemonade server. No telemetry.
- **Don't take focus.** The status pill and anything else shown while speaking must never activate a window, or it would steal the user's selection.
- **Old settings must keep loading.** `Settings::from_json` is lenient on purpose: unknown keys are ignored, bad values fall back to defaults. When you rename or remove a setting, keep reading the old name, and add a test.

## Style

- Format with `cargo fmt` (the project uses a 120-column width, see `rustfmt.toml`).
- Write code that reads like the code around it. Comments explain why, not what, and stay short.
- Commit messages: a one-line summary ending in a full stop, then a short paragraph or a few bullets on what changed and why, as in `git log`.
- One topic per pull request. Small is easier to review.

## Making a release

Maintainers:

1. Set the version in `Cargo.toml` and add its section to `CHANGELOG.md` (`## vX.Y.Z`, written for users).
2. Merge to `main`.
3. Push the tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.

The **Release** workflow tests and builds the Windows installer and zip, the macOS app and the Linux tarball, and publishes them with that version's changelog section as the release notes.

## License

By contributing you agree that your contributions are licensed under the project's [MIT license](LICENSE).
