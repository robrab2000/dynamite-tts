# Changelog

The release workflow publishes the section for each tag as that release's notes.

## v0.3.0

Summary and Explain no longer need anything installed, and a new ELI5 mode explains a highlight in the simplest terms.

### New

- **Summary and Explain work out of the box.** A small Qwen3 chat model (Apache 2.0) now runs inside the app with llama.cpp, on the GPU on Apple Silicon and the CPU elsewhere. It downloads the first time you use Summary or Explain, pinned and checked like the Phonon model, and is freed after 5 minutes unused.
- **Two model sizes.** Settings → Modes offers Qwen3 1.7B (recommended; 1.1 GB download, up to about 1.7 GB of memory while loaded) and Qwen3 0.6B (400 MB, about 1.0 GB) for machines with less memory. The picker shows each model's estimated memory use.
- **ELI5 mode.** A fourth speak mode, after Explain, that explains the highlight as if to a five-year-old, with simple words and an everyday comparison. On macOS and Linux it reads the paragraphs around the highlight to work out what it means there (on Linux through AT-SPI, which it turns on for Chromium and Electron apps). Like Summary and Explain it uses the built-in chat model, or Lemonade if you've chosen that.

### Changed

- **Lemonade is optional.** It stays available as a chat backend (Settings → Modes → Chat model → Lemonade), mainly for AMD Ryzen AI NPUs. The installers no longer install it unless you ask: the Windows option is unticked, and the macOS and Linux scripts take `--lemonade`.
- Building from source now needs CMake (and libclang on Linux) for llama.cpp; `cargo build --no-default-features` leaves it out.

### Upgrading

- Settings from 0.2.0 switch Summary, Explain and ELI5 to the built-in model. If you'd rather keep using Lemonade, choose it in Settings → Modes; your Lemonade model and address are kept.

## v0.2.0

A rewrite: one Rust app for Windows, macOS and Linux, replacing the separate C#, Swift and Python apps.

### New

- **Tone control.** A three-band EQ (Settings → Voice) with presets (Flat, Warm, Clear, Bright, Soft) or your own Low / Mid / High settings. Changes are heard straight away, even mid-sentence, with no added latency.
- **A new status pill.** It fades in and out, shows an orange ripple while thinking and blue bars that follow the voice while speaking, and stops speech when clicked. Choose Off, Minimal, Standard or Detailed in Settings → General, with a Preview button.
- **A new logo and icons.** The tray icon is grey when ready, orange while thinking and blue while speaking, with the fuse lit only while it's busy.
- **A redesigned Settings window** with Voice, Modes, Shortcuts and General pages, a folder picker for the Phonon model, and changes saved as you make them.
- **The same features everywhere.** Shortcuts you can rebind, start at login, the stop sound and the command line now work alike on all three systems.
- **A macOS installer.** Download **Install-Dynamite-TTS-macOS** and double-click the `.command` inside, with a README that walks through it. It fetches the app, copies it to ~/Applications, installs Lemonade and its chat model if you want them, and opens the app.

### Changed

- **Phonon is the only voice engine.** Kokoro has been removed, including on Windows its 325 MB model and CUDA support; the installer removes the old files. Lemonade is now needed only for Summary and Explain.
- On macOS the status pill sits just under the menu bar (and the notch), with rounded corners.
- Choosing Settings… while Settings is open brings the window to the front.
- The tray menu is now Mode, Settings… and Quit; stop speech with the shortcut or by clicking the pill.

### Upgrading

- Windows: run the new installer over the old one. It keeps the install folder, Start menu entry, login item and settings.
- Linux: run the new `install.sh`; your settings carry over and the GNOME shortcuts are updated to the new app.
- macOS: run **Install-Dynamite-TTS.command** (it replaces the app in ~/Applications; delete an old copy in /Applications). Settings start fresh.
- x86-64 builds need a CPU with AVX2 (about 2013 or newer).
