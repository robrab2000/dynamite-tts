# Dynamite TTS — Highlight-to-Speech for Windows, macOS, and Linux

A fast, lightweight, and 100% private highlight-to-speech utility. The Windows tray app is in `windows/` and runs Kokoro in-process. The Apple Silicon menu-bar app is in `macos/` and speaks through Lemonade’s Metal Kokoro, summarizing with Lemonade’s Metal chat model. The Linux tray app is in `linux/` and uses Lemonade’s CPU Kokoro and llama.cpp backends the same way.

```bash
./macos/install-lemonade.sh
./macos/package-app.sh
open macos/DynamiteTTS/DynamiteTTS.app
```

To carry it to another Apple Silicon Mac, build one installer and copy that file:

```bash
./macos/make-installer.sh ~/Desktop/DynamiteTTS-install.command
```

Copy that one file to the other Apple Silicon Mac and run it from Terminal. Opening it from Finder makes macOS treat the download as untrusted and move it to the Trash.

```bash
cd ~/Downloads
bash DynamiteTTS-install.command
```

The script prints each stage and stops on the line that fails. The window stays open until you press Return. It installs the Command Line Tools, Lemonade, the speech models, and the menu-bar app. It needs Apple Silicon and macOS 15 or later. The prompts you accept by hand are the Command Line Tools install and Accessibility for Dynamite TTS.

## Linux

A GTK tray app for GNOME (Wayland or X11) and other desktops. Like the Mac app it speaks through Lemonade, so there is nothing to compile: it needs only Python 3 with the GTK bindings that Ubuntu already ships.

```bash
./linux/install.sh
```

The script installs the system packages with `apt` (asks for your password once), puts Lemonade and the app under `~/.local`, downloads the Kokoro voice and the chat model, and starts the tray icon. It needs Ubuntu 24.04 or later, or any distribution with Python 3.11+, GTK 3, libnotify, and PipeWire or PulseAudio. Highlight some text and press `Ctrl+Shift+S`; `Ctrl+Shift+M` cycles Verbatim → Summary → Explain.

How it differs from the other platforms:

- **No clipboard tricks.** On Linux a highlight *is* the PRIMARY selection, so the app reads it directly with `wl-paste` (Wayland) or `xclip` (X11). Your clipboard is never touched, and no fake `Ctrl+C` is sent.
- **Shortcuts belong to the desktop.** Wayland does not let a background app grab keys. On GNOME the app registers two custom shortcuts (Settings → Keyboard → Custom Shortcuts) that run `dynamite-tts speak` and `dynamite-tts toggle-mode`, and those commands reach the running app over a Unix socket. On KDE, Sway, or Hyprland bind those two commands yourself; they work the same.
- **Status is a notification.** A compositor will not let an app float a pill at the top of the screen without taking focus, so phases (`Summarizing…`, `Speaking`) are a desktop notification with a Stop button that is updated in place and closed when speech ends. GNOME draws it top-centre.
- **Tray icon via AppIndicator.** Ubuntu's GNOME has the AppIndicator extension on by default; on stock GNOME enable it (or use the CLI and the Settings window without a tray).

Useful commands:

```bash
dynamite-tts              # start (or raise Settings if already running)
dynamite-tts speak        # read the highlight (what the shortcut runs)
dynamite-tts toggle-mode  # Verbatim → Summary → Explain
dynamite-tts stop | settings | ping | quit
dynamite-tts --speak "Hello"
./linux/uninstall.sh      # remove the app, shortcuts, and settings (add --purge for Lemonade too)
```

Settings live in `~/.config/dynamite-tts/settings.json`, the log in `~/.local/state/dynamite-tts/dynamite.log`, and Lemonade plus its models in `~/.local/share/dynamite-tts/lemonade`. To run from the checkout without installing: `./linux/dynamite-tts`. Unit tests: `cd linux && python3 -m unittest discover -s tests -t .`.

Measured on a 6-core i7-8750H (CPU only): about 1 s to first audio for a short sentence, 2.5–3 s for a paragraph, and synthesis runs about 3x realtime, so playback starts on the first chunk while the rest renders. The optional `llamacpp:vulkan` backend speeds up Summary and Explain on a GPU: `~/.local/share/dynamite-tts/lemonade/lemonade backends install llamacpp:vulkan`.

---

## Features

- **Tray-First Native Architecture:** Sits quietly in the Windows notification area with a lightweight Win32 message-only host (`HWND_MESSAGE`) and custom tray menu.
- **Dynamic 3-State Tray Icon:**
  - **Ready:** Solid icon with tooltip `Dynamite TTS (Ready)`
  - **Synthesizing:** Active icon with tooltip `Dynamite TTS (Synthesizing...)`
  - **Speaking:** Audio wave icon with tooltip `Dynamite TTS (Speaking...)`
- **Global Hotkey (Default: `Ctrl+Shift+S`):**
  - Instant text-to-speech from any application (browser, PDF, code editor, office apps).
  - Modifier key debounce so standard `Ctrl+C` copy operates reliably in every application.
  - Interactive "Press Shortcut" recorder in Settings with dynamic conflict detection and instant rebinding (no restart required).
- **Speak Mode Toggle (Default: `Ctrl+Shift+M`):**
  - Cycles **Verbatim** → **Summary** → **Explain** → **Verbatim**.
  - **Verbatim** speaks the selection as-is; **Summary** summarizes first, then speaks; **Explain** briefly explains the selection (names it up front; adapts for short terms vs longer passages), then speaks. For short highlights, Explain tries to read surrounding paragraph/nearby lines via UI Automation so ambiguous words (e.g. product names) can be disambiguated.
  - Summary and Explain call a local [Lemonade Server](https://lemonade-server.ai) LLM via OpenAI-compatible `/chat/completions` — text stays on-device. TTS can still use in-process Kokoro.
  - Explain length (about N sentences) and audience (Technical / General) are configurable in Settings; they share the **Chat Model** with Summary.
  - The status bubble shows “Mode: …” on toggle, and “Summarizing…” / “Explaining…” while the LLM runs.
- **Clipboard Preservation:**
  - Safely captures full COM `IDataObject` preserving all existing clipboard content (including images, screenshots, files, and formatted text).
  - Automatically restores your previous clipboard content after extracting the selected text.
  - Empty or non-text selections are silently ignored.
- **Smart Text Sanitization & Chunking:**
  - Cleans markdown links, raw URLs, code backticks, header hashes, and bullet markers for natural reading flow.
  - Automatically splits long articles (>500 characters) across sentence boundaries and streams chunks sequentially with lookahead pre-buffering.
- **In-Process Local Kokoro Engine (default):**
  - Runs Kokoro-82M through ONNX Runtime inside the app; no text leaves the machine.
  - Streams speech sentence by sentence: a short first segment plays within about a second while the rest renders in the background through one gapless audio stream with back-pressure.
  - Stop or a new hotkey press cancels the in-flight inference immediately (no orphaned work queued behind the next request).
  - CPU path (one thread per physical core, fp32 graph) is always warm: about 0.35 s to first audio and 5x realtime on a Ryzen AI 9 HX 370.
  - NVIDIA GPUs are used through CUDA when present: the CUDA 13 runtime (about 1 GB) is fetched once from NVIDIA's PyPI wheels into `%LocalAppData%\DynamiteTts\cuda13`, the GPU session loads on first use while the CPU renders the first sentences, and it is unloaded after a few idle minutes so a laptop's discrete GPU can sleep. Measured on an RTX 5070 Laptop: about 0.11 s to first audio and 13x realtime.
  - "CPU only" in Settings keeps the GPU untouched; DirectML and the Ryzen AI NPU were evaluated and rejected (see `windows/experiments/FINDINGS-tts-backends.md`).
  - Playback starts as soon as the first sentence is rendered (about 0.2 s with the GPU session loaded, 0.6–0.8 s on the CPU) and continues while the rest of the selection is synthesized.
- **Status Bubble:**
  - A small pill at the top of the screen, in the style of Handy's recording bubble, shows "Reading selection…", "Summarizing…", "Explaining…", "Synthesizing…" and "Speaking", with level bars that follow the actual audio output and a stop button.
  - It never takes keyboard focus, so it cannot interfere with the selection capture or with typing. It can be turned off in Settings.
- **Diagnostics Log:**
  - Each request's timings (time to first audible audio, synthesis and playback duration, mid-speech gaps, fallbacks) and engine state changes are written to `%LocalAppData%\DynamiteTts\logs`, kept for a week. The spoken text itself is never logged.
- **Reliable Local Audio Engine:**
  - Uses NAudio with WASAPI Shared mode (`WasapiOut`) so speech mixes naturally with background music or system audio.
  - Configurable audio output device with automatic fallback to Windows System Default on disconnect.
  - Single-concurrency speech pipeline: Pressing the hotkey again or hitting **Stop** instantly halts playback and clears in-flight synthesis.
- **Accessible Modern WPF Settings:**
  - Dark/Light adaptive styling matching Windows system themes.
  - Live server connection health badge (Green dot when online, Red when offline).
  - Direct **Voice Preview / Audition** button to test different Kokoro and OpenAI voice accents.
  - Output device selector and speech speed adjustment (0.25x - 4.0x).
  - Optional **Launch at login** toggle backed by `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
  - Double-clicking the tray icon opens Settings.
- **Single-Instance Enforcement:** Named mutex activation brings existing Settings window to the foreground if launched again.

---

## Architecture & Layout

```
windows/
├── DynamiteTts.sln
├── src/
│   └── DynamiteTts/
│       ├── App.xaml / App.xaml.cs            # App bootstrap, single-instance mutex, shutdown
│       ├── Native/
│       │   ├── NativeMethods.cs              # Shell_NotifyIcon, RegisterHotKey, SendInput, Menus
│       │   ├── TrayIconHost.cs               # HWND_MESSAGE, 3-state icon switching, context menu
│       │   └── HotkeyService.cs              # Win32 RegisterHotKey (speak + mode-toggle IDs), conflict detection & rebind
│       ├── Services/
│       │   ├── AppSettingsStore.cs           # %AppData%\DynamiteTts\settings.json
│       │   ├── AppLog.cs                     # Daily rolling diagnostics log in %LocalAppData%\DynamiteTts\logs (timings only, never text)
│       │   ├── ClipboardSelectionService.cs  # Full IDataObject preserve/restore & SendInput Ctrl+C
│       │   ├── TextSanitizer.cs              # Markdown & URL cleaner for natural TTS reading
│       │   ├── SentenceChunker.cs            # Boundary splitting & pipelining for long articles
│       │   ├── LocalTtsService.cs            # In-process Kokoro via ONNX Runtime: segmentation, streaming, cancellation, CPU + lazily loaded CUDA session
│       │   ├── CudaRuntimeService.cs         # Downloads/extracts the CUDA 13 DLLs (cudart, cuBLAS, cuDNN, cuFFT, NVRTC) from NVIDIA's PyPI wheels
│       │   ├── CudaDeviceService.cs          # NVIDIA GPU enumeration via the CUDA driver API; accelerator picker entries
│       │   ├── LemonadeTtsClient.cs          # HTTP client, endpoint normalizer, model discovery
│       │   ├── LemonadeChatClient.cs         # Local Lemonade /chat/completions for Summary and Explain modes
│       │   ├── AudioDeviceService.cs         # Output device enumeration (MMDevice)
│       │   ├── AudioPlaybackService.cs       # NAudio WASAPI Shared playback & cancellation
│       │   ├── SpeechOrchestrator.cs         # Speech lifecycle, verbatim/summary/explain mode, state machine, cancellation
│       │   ├── StartupRegistrationService.cs # HKCU Run registry helper
│       │   └── TrayNotificationService.cs    # Native Windows balloon notifications
│       ├── Models/
│       │   ├── AppSettings.cs
│       │   ├── AudioDeviceInfo.cs
│       │   ├── HotkeyConfig.cs
│       │   ├── SpeakMode.cs                  # Verbatim | Summary | Explain
│       │   ├── ExplainAudience.cs            # General | Technical
│       │   ├── TrayIconState.cs
│       │   ├── TtsModelInfo.cs
│       │   └── VoicePreset.cs
│       ├── UI/
│       │   ├── SettingsWindow.xaml(+.cs)     # Dark/Light adaptive Settings window
│       │   ├── StatusBubbleWindow.xaml(+.cs) # Top-of-screen status pill (reading / summarizing / explaining / synthesizing / speaking)
│       │   └── Controls/
│       │       └── HotkeyCaptureBox.xaml(+.cs)# Interactive shortcut capture box
│       └── Resources/
│           ├── app_idle.ico                  # Ready tray icon
│           ├── app_active.ico                # Synthesizing tray icon
│           └── app_speaking.ico              # Speaking tray icon
└── tests/
    └── DynamiteTts.Tests/                   # xUnit unit test suite
macos/
└── DynamiteTTS/                             # Apple Silicon menu-bar app
linux/
├── dynamite-tts                             # Launcher (python3 -m dynamite_tts)
├── install.sh / install-lemonade.sh / uninstall.sh
├── dynamite_tts/
│   ├── app.py                               # AppIndicator tray, menu, phases, control-socket commands
│   ├── speech.py                            # Speech lifecycle: cleanup → chat (Summary/Explain) → streamed Kokoro
│   ├── lemonade.py                          # Lemonade HTTP client: health, load/pull, chat, PCM stream; starts lemond
│   ├── playback.py                          # PCM piped to pw-play / paplay / aplay from a writer thread; chime; levels
│   ├── selection.py                         # PRIMARY selection via wl-paste / xclip / xsel / GTK (clipboard fallback)
│   ├── hotkeys.py                           # GNOME custom keybindings → `dynamite-tts speak` / `toggle-mode`
│   ├── control.py                           # Single-instance lock and the Unix control socket
│   ├── status.py                            # Phase notifications with a Stop action (libnotify)
│   ├── settings.py / settings_window.py     # ~/.config/dynamite-tts/settings.json and the GTK Settings window
│   ├── text.py                              # Same sanitizer and Summary/Explain prompts as the other platforms
│   └── autostart.py                         # ~/.config/autostart entry for Launch at login
└── tests/                                   # unittest suite (no audio hardware needed)
```

---

## Prerequisites (Windows)

- Windows 10 (1809+) or Windows 11 (x64)
- [.NET 9.0 SDK](https://dotnet.microsoft.com/) (for building from source only)
- Optional: local [Lemonade Server](https://lemonade-server.ai) on `http://localhost:13305` for Summary / Explain modes or Lemonade-server TTS (default in-process Kokoro does not need it)

---

## Install (prebuilt)

1. Download **`DynamiteTts-Setup-v*.exe`** from [Releases](https://github.com/robrab2000/dynamite-tts/releases) (preferred), or the portable `.zip`.
2. Run the installer — it explains dependencies, checks for 64-bit Windows 10 1809+ / Windows 11, and installs to `%LocalAppData%\Programs\DynamiteTTS`.
3. On the optional-components page, **Lemonade Server is checked by default** (needed for Summary / Explain). Uncheck it if you only want Verbatim TTS.
4. When setup finishes, choose **Launch Dynamite TTS**. The tray icon appears; highlight text and press the speak hotkey (`Ctrl+Shift+S` by default).

The installer is self-contained (no separate .NET runtime). It bundles the Kokoro model and voices so Verbatim speech works immediately. The first time you use **Summary** or **Explain** mode, Dynamite downloads a small default Lemonade chat model (`Qwen2.5-0.5B-Instruct`) if needed. Optional NVIDIA CUDA acceleration (~1 GB) still downloads on first GPU use.

### Hardware

- **Verbatim (Kokoro):** any supported Windows x64 PC (CPU; NVIDIA GPU optional).
- **Summary / Explain (Lemonade):** needs Lemonade Server plus enough RAM for a small local LLM. AMD NPU/GPU is optional and only speeds things up — not required for the default tiny model.

**SmartScreen:** builds are currently **unsigned**. Windows may show “Windows protected your PC” — choose **More info** → **Run anyway**.

Portable zip users: unzip somewhere permanent, keep `voices` (and `kokoro.onnx` if present) next to `DynamiteTts.exe`, then run the exe. Install Lemonade separately if you want Summary or Explain mode.

---

## Quick Start (from source)

### 1. Build and Run
```powershell
dotnet run --project windows/src/DynamiteTts/DynamiteTts.csproj
```

### 2. Publish Self-Contained Executable
```powershell
./windows/publish.ps1
```
The self-contained `DynamiteTts.exe` will be generated in `windows/artifacts/publish/`, together with the Kokoro `voices` folder and `kokoro.onnx` (downloaded during publish when missing). `windows/publish.ps1` also writes `windows/artifacts/DynamiteTts-win-x64-<version>.zip` and, when [Inno Setup](https://jrsoftware.org/isinfo.php) is installed, `windows/artifacts/DynamiteTts-Setup-v<version>.exe`.

---

## Tray Menu Actions

- **Stop (Esc):** Instantly halts current audio playback and cancels pending synthesis.
- **Settings... (Default / Double Click):** Opens the configuration window.
- **Refresh Models:** Queries Lemonade Server for newly installed or downloaded TTS models.
- **Exit:** Completely shuts down Dynamite TTS, cleans up tray icons, unregisters hotkeys, and releases resources.
