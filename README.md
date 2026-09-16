# Dynamite TTS — Windows Highlight-to-Speech

A fast, lightweight, and 100% private Windows highlight-to-speech utility powered by local TTS engines (such as [Lemonade Server](https://lemonade-server.ai) running Kokoro or OpenMOSS).

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
- **Summary / Verbatim Mode Toggle (Default: `Ctrl+Shift+M`):**
  - Flips between **Verbatim** (speak the selection as-is) and **Summary** (summarize first, then speak).
  - Summary mode calls a local [Lemonade Server](https://lemonade-server.ai) LLM via OpenAI-compatible `/chat/completions` — text stays on-device. TTS can still use in-process Kokoro.
  - The status bubble shows “Mode: Summary” / “Mode: Verbatim” on toggle, and “Summarizing…” while the LLM runs.
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
  - "CPU only" in Settings keeps the GPU untouched; DirectML and the Ryzen AI NPU were evaluated and rejected (see `experiments/FINDINGS-tts-backends.md`).
  - Playback starts as soon as the first sentence is rendered (about 0.2 s with the GPU session loaded, 0.6–0.8 s on the CPU) and continues while the rest of the selection is synthesized.
- **Status Bubble:**
  - A small pill at the top of the screen, in the style of Handy's recording bubble, shows "Reading selection…", "Summarizing…", "Synthesizing…" and "Speaking", with level bars that follow the actual audio output and a stop button.
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
DynamiteTts.sln
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
│       │   ├── LemonadeChatClient.cs         # Local Lemonade /chat/completions for Summary mode
│       │   ├── AudioDeviceService.cs         # Output device enumeration (MMDevice)
│       │   ├── AudioPlaybackService.cs       # NAudio WASAPI Shared playback & cancellation
│       │   ├── SpeechOrchestrator.cs         # Speech lifecycle, verbatim/summary mode, state machine, cancellation
│       │   ├── StartupRegistrationService.cs # HKCU Run registry helper
│       │   └── TrayNotificationService.cs    # Native Windows balloon notifications
│       ├── Models/
│       │   ├── AppSettings.cs
│       │   ├── AudioDeviceInfo.cs
│       │   ├── HotkeyConfig.cs
│       │   ├── SpeakMode.cs                  # Verbatim | Summary
│       │   ├── TrayIconState.cs
│       │   ├── TtsModelInfo.cs
│       │   └── VoicePreset.cs
│       ├── UI/
│       │   ├── SettingsWindow.xaml(+.cs)     # Dark/Light adaptive Settings window
│       │   ├── StatusBubbleWindow.xaml(+.cs) # Top-of-screen status pill (reading / summarizing / synthesizing / speaking)
│       │   └── Controls/
│       │       └── HotkeyCaptureBox.xaml(+.cs)# Interactive shortcut capture box
│       └── Resources/
│           ├── app_idle.ico                  # Ready tray icon
│           ├── app_active.ico                # Synthesizing tray icon
│           └── app_speaking.ico              # Speaking tray icon
└── tests/
    └── DynamiteTts.Tests/                   # xUnit unit test suite
```

---

## Prerequisites

- Windows 10 (1809+) or Windows 11 (x64)
- [.NET 9.0 SDK](https://dotnet.microsoft.com/) (for building from source only)
- Optional: local [Lemonade Server](https://lemonade-server.ai) on `http://localhost:13305` for Summary mode or Lemonade-server TTS (default in-process Kokoro does not need it)

---

## Install (prebuilt)

1. Download **`DynamiteTts-Setup-v*.exe`** from [Releases](https://github.com/robrab2000/dynamite-tts/releases) (preferred), or the portable `.zip`.
2. Run the installer — it checks for 64-bit Windows 10 1809+ / Windows 11, installs to `%LocalAppData%\Programs\DynamiteTTS`, adds a Start Menu shortcut, and can start Dynamite TTS at sign-in.
3. When setup finishes, choose **Launch Dynamite TTS** (default). The tray icon appears; highlight text and press the speak hotkey (`Ctrl+Shift+S` by default).

The installer is self-contained (no separate .NET runtime). It bundles the Kokoro model and voices so speech works immediately. Optional NVIDIA CUDA acceleration (~1 GB) still downloads on first GPU use. Summary mode needs a separate [Lemonade Server](https://lemonade-server.ai) install.

**SmartScreen:** builds are currently **unsigned**. Windows may show “Windows protected your PC” — choose **More info** → **Run anyway**.

Portable zip users: unzip somewhere permanent, keep `voices` (and `kokoro.onnx` if present) next to `DynamiteTts.exe`, then run the exe.

---

## Quick Start (from source)

### 1. Build and Run
```powershell
dotnet run --project src/DynamiteTts/DynamiteTts.csproj
```

### 2. Publish Self-Contained Executable
```powershell
./publish.ps1
```
The self-contained `DynamiteTts.exe` will be generated in `artifacts/publish/`, together with the Kokoro `voices` folder and `kokoro.onnx` (downloaded during publish when missing). `publish.ps1` also writes `artifacts/DynamiteTts-win-x64-<version>.zip` and, when [Inno Setup](https://jrsoftware.org/isinfo.php) is installed, `artifacts/DynamiteTts-Setup-v<version>.exe`.

---

## Tray Menu Actions

- **Stop (Esc):** Instantly halts current audio playback and cancels pending synthesis.
- **Settings... (Default / Double Click):** Opens the configuration window.
- **Refresh Models:** Queries Lemonade Server for newly installed or downloaded TTS models.
- **Exit:** Completely shuts down Dynamite TTS, cleans up tray icons, unregisters hotkeys, and releases resources.
