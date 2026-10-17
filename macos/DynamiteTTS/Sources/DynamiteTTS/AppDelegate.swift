import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    static weak var shared: AppDelegate?

    let settings = SettingsStore()
    private(set) var speech: SpeechSession!
    private var statusItem: NSStatusItem!
    private var menu: NSMenu!
    private var settingsWindow: SettingsWindowController!
    private var statusPanel: StatusPanel!
    private var hotkeys: HotkeyCenter!
    private var modeItem: NSMenuItem!

    func applicationDidFinishLaunching(_ notification: Notification) {
        speech = SpeechSession(settings: settings)
        speech.onPhase = { [weak self] phase in self?.showPhase(phase) }
        speech.onNotice = { [weak self] text in self?.showNotice(text) }

        statusPanel = StatusPanel { [weak self] in
            self?.speech.stop(playChime: true)
        }
        settingsWindow = SettingsWindowController(store: settings) { [weak self] in
            self?.applySettings()
        }
        hotkeys = HotkeyCenter()
        hotkeys.onSpeak = { [weak self] in self?.speakSelection() }
        hotkeys.onToggleMode = { [weak self] in self?.cycleMode() }

        buildStatusItem()
        applySettings()

        if let text = LaunchArguments.speakText {
            speech.speak(text: text)
        }
        if !SelectionReader.isTrusted(prompt: true) {
            SelectionReader.openAccessibilitySettings()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        speech.stop(playChime: false)
    }

    private func buildStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = StatusIcon.image
            button.toolTip = "Dynamite TTS"
        }
        menu = NSMenu()
        menu.addItem(NSMenuItem(title: "Stop", action: #selector(stopSpeech), keyEquivalent: ""))
        modeItem = NSMenuItem(title: "Mode: Verbatim", action: #selector(cycleModeFromMenu), keyEquivalent: "")
        menu.addItem(modeItem)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Settings…", action: #selector(openSettings), keyEquivalent: ","))
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q"))
        for item in menu.items { item.target = self }
        statusItem.menu = menu
    }

    private func applySettings() {
        hotkeys.rebind(speak: settings.current.speakHotkey, mode: settings.current.modeHotkey)
        modeItem.title = "Mode: \(settings.current.speakMode.title)"
        if settings.current.launchAtLogin && LaunchAtLogin.isBundledApp {
            try? LaunchAtLogin.apply(true)
        }
    }

    private func setMode(_ mode: SpeakMode) {
        settings.update { $0.speakMode = mode }
        applySettings()
        showNotice("Mode: \(mode.title)")
    }

    private func cycleMode() {
        setMode(settings.current.speakMode.next())
    }

    private func speakSelection() {
        let includeContext = settings.current.speakMode == .explain
        if !SelectionReader.isTrusted(prompt: false) {
            _ = SelectionReader.isTrusted(prompt: true)
        }
        let captured = SelectionReader.capture(includeContext: includeContext)
        guard let text = captured.text else {
            showNotice("No text selected")
            return
        }
        speech.speak(text: text, context: captured.context)
    }

    private func showPhase(_ phase: String?) {
        let title = phase ?? "Ready"
        AppLog.write("phase: \(title)")
        statusItem.button?.image = StatusIcon.image(for: phase)
        statusItem.button?.toolTip = "Dynamite TTS (\(title))"
        guard settings.current.showStatusPanel else {
            statusPanel.hide()
            return
        }
        if let phase {
            statusPanel.show(phase)
        } else {
            statusPanel.hide()
        }
    }

    private func showNotice(_ text: String) {
        AppLog.write("notice: \(text)")
        if settings.current.showStatusPanel {
            statusPanel.showNotice(text)
        }
    }

    @objc private func stopSpeech() { speech.stop(playChime: true) }
    @objc private func cycleModeFromMenu() { cycleMode() }
    @objc private func openSettings() { settingsWindow.show() }
    @objc private func quit() { NSApp.terminate(nil) }
}

enum StatusIcon {
    static var image: NSImage { image(named: "app_idle") }

    static func image(for phase: String?) -> NSImage {
        switch phase {
        case "Speaking":
            return image(named: "app_speaking")
        case nil:
            return image(named: "app_idle")
        default:
            return image(named: "app_active")
        }
    }

    private static func image(named name: String) -> NSImage {
        let loaded = Bundle.main.url(forResource: name, withExtension: "png").flatMap { NSImage(contentsOf: $0) }
        let image = loaded ?? NSImage(size: NSSize(width: 18, height: 18))
        image.isTemplate = false
        image.size = NSSize(width: 18, height: 18)
        return image
    }
}

enum LaunchArguments {
    static let speakText: String? = {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: "--speak"), index + 1 < args.count else { return nil }
        return args[index + 1]
    }()
}
