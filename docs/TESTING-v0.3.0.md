# Testing v0.3.0

What changed in this release and how to check it on each platform. Tick off each item on the
platform it applies to. Release downloads: https://github.com/robrab2000/dynamite-tts/releases/tag/v0.3.0

| Platform | Download | Tester |
|---|---|---|
| Windows 10/11 x64 | `DynamiteTts-Setup-v0.3.0.exe` (also the portable `DynamiteTts-win-x64-v0.3.0.zip`) | |
| macOS 14+ Apple Silicon | `Install-Dynamite-TTS-macOS-v0.3.0.zip` (also `DynamiteTTS-macos-arm64-v0.3.0.zip` for offline) | |
| Linux (Ubuntu 22.04+, GNOME) | `DynamiteTts-linux-x64-v0.3.0.tar.gz` | |

Keep the log open while testing. It is under Settings → General → Log file, and every chat request
logs its mode, backend, size and timing (never the text itself).

## 1. Installers

The chat model for Summary, Explain and ELI5 is now built in, so Lemonade is an opt-in extra on
every platform. Check that each installer reflects that.

- [ ] **Windows.** Run the Setup exe over 0.2.0 (upgrade) and on a clean machine. On the
      optional-components page, "Install Lemonade Server" is **unticked** by default. The install
      finishes without winget being called unless you tick it. The install folder, Start menu
      entry, login item and settings survive the upgrade.
- [ ] **macOS.** Open `Install-Dynamite-TTS-macOS-v0.3.0.zip`, double-click
      `Install-Dynamite-TTS.command`. It downloads the app zip, asks "Also install Lemonade?
      [y/N]", and Return (no) skips Lemonade. The app lands in `~/Applications` and opens. Check
      the Gatekeeper "Open Anyway" path described in README.txt still works. Repeat with the
      offline `DynamiteTTS-macos-arm64` zip, whose installer should use the app next to it
      instead of downloading.
- [ ] **Linux.** Unpack the tarball and run `./install.sh`. It should not install Lemonade;
      `./install.sh --lemonade` should. The GNOME shortcuts are bound and the app starts from the
      launcher.
- [ ] **Uninstall notes** in the Windows Dependencies page and macOS README.txt still match
      what is on disk (the chat models live under the app's data folder, `models/chat`).

## 2. Summary and Explain on the built-in model (all platforms)

- [ ] **First use.** Fresh install, Settings → Modes → Chat model is **Built in** with
      **Qwen3 1.7B** selected. Highlight a paragraph, press the speak shortcut in Summary mode.
      The status pill shows "Downloading the chat model (10%)…" climbing in tens, then "Loading
      the chat model…", then "Summarizing…", then speech. The download is 1.1 GB.
- [ ] **Warm request.** A second Summary straight after skips the download and load phases and
      starts speaking within a few seconds (M-series Mac: 1 to 3 s; CPU-only: longer, note the
      time in the log).
- [ ] **Explain** works the same way, with "Explaining…" shown, and still honours Length and
      Written for in Settings → Modes.
- [ ] **Model switch.** Change Model to Qwen3 0.6B. The row shows the download size and
      estimated memory. The next request downloads 400 MB, and the log shows `qwen3-0.6b`
      loaded. Switch back; the 1.7B model is not re-downloaded.
- [ ] **Idle unload.** Leave the app for 5 minutes after a request. The log shows "unloading the
      chat model after 300 s idle" and the process memory drops by roughly the model size
      (Activity Monitor / Task Manager / `top`). The next request reloads it.
- [ ] **Cancel mid-way.** Press the stop shortcut or click the pill while "Summarizing…" is
      showing. Speech never starts and the pill clears.
- [ ] **Long input.** Highlight a few pages of text. The log notes "input too long … keeping N
      chars" and a summary is still spoken.
- [ ] **Warm-up.** Switching to Summary, Explain or ELI5 mode logs "llm: llama.cpp ready" within
      a few seconds (this precompiles the GPU kernels on a Mac).
- [ ] **Upgrade from 0.2.0.** With a 0.2.0 settings file that named a Lemonade model, the app
      starts with Chat model = Built in, and the old Lemonade model name is still shown if you
      switch Chat model to Lemonade.
- [ ] **Low memory (if you have an 8 GB machine).** With the 1.7B model, a request either
      works or fails with the title "Not enough memory for the chat model", and the 0.6B model
      then works.

## 3. Lemonade still works as the alternative backend

- [ ] Settings → Modes → Chat model → **Lemonade**. The Lemonade model and Server address rows
      appear, with a Refresh button. With Lemonade installed, Summary, Explain and ELI5 run
      through it ("Starting Lemonade…" if it was not running). Without it installed, the error
      title is "Install Lemonade for Summary" (or Explain / ELI5), not a crash.
- [ ] **Windows with an AMD Ryzen AI NPU** (rob_aero): tick Lemonade in the installer, choose it
      in Settings, and confirm the NPU path still answers.

## 4. ELI5 mode

ELI5 is the fourth mode after Explain. For a short highlight it first works out what the
highlight means in the surrounding text, then explains it in up to four plain sentences with an
everyday comparison.

- [ ] **Mode cycling.** The change-mode shortcut (default Ctrl/Control-Shift-M) goes Verbatim →
      Summary → Explain → ELI5 → Verbatim, with the notice "Mode: ELI5". The tray menu's Mode
      submenu lists ELI5 and ticks it. Settings → Modes → Speak mode shows ELI5.
- [ ] **Short highlight with context (macOS and Linux).** In a web page or editor, highlight one
      word that is ambiguous out of context, such as "Kafka" in a sentence about message
      queues, and press speak. The pill shows "Simplifying…". The log shows "eli5 meaning: found"
      and the spoken explanation matches the sense used in the text (the queue, not the writer).
      Repeat with the same word in a different sense.
- [ ] **Long highlight.** Highlight a whole paragraph in ELI5 mode. No meaning step runs (log:
      no "eli5 meaning" line) and the explanation is at most four sentences.
- [ ] **Windows.** ELI5 works on a highlight but has no surrounding text (Windows reads the
      clipboard only), so the meaning step is skipped and the explanation is of the highlight
      alone. Confirm it still speaks and does not error.
- [ ] **Linux context sources.** Try a GTK app (gedit / Text Editor), Firefox, and a Chromium or
      Electron app (Chrome, VS Code). For Chromium and Electron the app turns on the AT-SPI
      screen-reader flag while an ELI5 or Explain mode is selected; check with
      `gsettings get org.gnome.desktop.a11y.applications screen-reader-enabled` or
      `busctl --user get-property org.a11y.Bus /org/a11y/bus org.a11y.Status ScreenReaderEnabled`,
      and that it is put back when you switch to Verbatim or quit.
- [ ] **macOS permission.** With Accessibility denied, ELI5 falls back to the pasteboard text
      and explains without context. With it allowed, the log shows a meaning was found.
- [ ] **Both backends.** Run the same ELI5 highlight with Chat model = Built in and = Lemonade;
      both answer and both log the backend name.
- [ ] **Empty or failed reply.** Highlight a lone punctuation mark. The notice is "The
      explanation was empty." or an "ELI5 failed" title, not a hang.

## 5. Regression (unchanged features, quick pass)

- [ ] **Verbatim** reads a highlight with Phonon offline (airplane mode) on first run after
      install, with no chat-model download triggered.
- [ ] **Stop** via shortcut and via clicking the pill, including mid-sentence.
- [ ] **Tone** presets and sliders change the voice live.
- [ ] **Status pill** styles Off / Minimal / Standard / Detailed and the Preview button.
- [ ] **Shortcuts** rebinding, Start at login, Stop sound, Output device.
- [ ] **CLI**: `dynamite-tts --speak "hello"` speaks verbatim even when ELI5 mode is selected.
- [ ] **Settings** survive quit and relaunch, including the Chat model and Model choices.

## 6. Build from source (one machine each)

- [ ] `cargo clippy --locked --all-targets -- -D warnings` and `cargo test --locked` pass. CMake
      is now needed (and libclang on Linux) for llama.cpp.
- [ ] `cargo build --no-default-features` builds without llama.cpp; in that build, Chat model =
      Built in gives the error "This build has no built-in chat model; choose Lemonade in
      Settings." and Lemonade still works.
- [ ] Ignored end-to-end checks, with the 1.7B model downloaded:
      `cargo test --release eli5_on_local_model -- --ignored --nocapture` and
      `cargo test --release real_chat -- --ignored --nocapture` (prints timings and peak memory).

## Known gaps going in

- Windows has no surrounding-text capture, so ELI5's meaning step never runs there.
- Builds are not code-signed; SmartScreen and Gatekeeper prompts are expected.
- x86-64 builds need AVX2; the built-in model runs on the CPU everywhere except Apple Silicon.
- The Settings "Not enough memory" guidance only appears in the README, not in the app.
