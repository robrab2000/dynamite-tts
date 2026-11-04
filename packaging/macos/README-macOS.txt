Dynamite TTS __VERSION__ for macOS
==================================

Reads highlighted text aloud. Needs an Apple Silicon Mac (M1 or newer)
running macOS 14 or later.


INSTALL

1. Double-click "Install-Dynamite-TTS.command" in this folder.

   Terminal opens, downloads the app (about 60 MB), and shows each step.
   The window stays open until you press Return.

2. If macOS says the file "cannot be opened" or "Apple could not verify" it:

   - Open System Settings > Privacy & Security.
   - Scroll down and click "Open Anyway" next to Install-Dynamite-TTS.
   - Double-click the installer again and choose Open.

   This happens because the download isn't notarized by Apple yet.

3. The installer asks whether to install Lemonade:

   - Y installs Lemonade Server and a small chat model (about 1 GB).
     Summary, Explain and ELI5 modes need it.
   - n skips it. Verbatim reading works without it.

4. Dynamite TTS opens in the menu bar. When macOS asks, allow it under
   System Settings > Privacy & Security > Accessibility. It needs this
   to read the text you highlight. Then quit and reopen it from the
   menu bar icon.


IF DOUBLE-CLICK DOESN'T WORK

Run the installer from Terminal instead:

1. Open Terminal (Applications > Utilities > Terminal).
2. Type "bash" followed by a space. Don't press Return yet.
3. Drag Install-Dynamite-TTS.command from Finder into the Terminal
   window. Its path appears after "bash ".
4. Press Return.

This also works if macOS says you don't have permission to open it.


USE IT

- Highlight text in any app and press Control-Shift-S to hear it.
- Press Control-Shift-S again to stop, or click the status pill.
- Control-Shift-M switches between Verbatim, Summary, Explain and ELI5.
- Settings are in the menu bar icon's menu.


WHAT IT INSTALLS

- The app: ~/Applications/DynamiteTTS.app
- Settings, the voice model and Lemonade (if chosen):
  ~/Library/Application Support/DynamiteTTS
- Logs: ~/Library/Logs/DynamiteTTS

No administrator password is needed. To uninstall, quit the app from
its menu bar icon and delete those three folders.


More help: https://github.com/robrab2000/dynamite-tts
