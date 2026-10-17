import AppKit
import ServiceManagement

@MainActor
final class SettingsWindowController: NSWindowController {
    private let store: SettingsStore
    private let onChange: () -> Void
    private var voicePopup: NSPopUpButton!
    private var speedSlider: NSSlider!
    private var speedLabel: NSTextField!
    private var devicePopup: NSPopUpButton!
    private var speakCapture: HotkeyCaptureField!
    private var modeCapture: HotkeyCaptureField!
    private var loginCheck: NSButton!
    private var loginStatus: NSTextField!
    private var panelCheck: NSButton!
    private var chimeCheck: NSButton!
    private var sentences: NSStepper!
    private var sentenceLabel: NSTextField!
    private var audiencePopup: NSPopUpButton!
    private var chatField: NSTextField!
    private var baseField: NSTextField!
    private var devices: [AudioOutputDevice] = []

    init(store: SettingsStore, onChange: @escaping () -> Void) {
        self.store = store
        self.onChange = onChange
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 480, height: 560),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Settings"
        window.minSize = NSSize(width: 420, height: 280)
        super.init(window: window)
        build()
        reload()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    func show() {
        reload()
        window?.center()
        showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    private func build() {
        guard let content = window?.contentView else { return }
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = false
        scroll.autohidesScrollers = false
        scroll.drawsBackground = false
        scroll.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(scroll)
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: content.topAnchor),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor),
        ])

        let document = FlippedDocumentView()
        document.translatesAutoresizingMaskIntoConstraints = false
        scroll.documentView = document

        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.translatesAutoresizingMaskIntoConstraints = false
        document.addSubview(stack)
        NSLayoutConstraint.activate([
            document.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor),
            stack.topAnchor.constraint(equalTo: document.topAnchor, constant: 16),
            stack.leadingAnchor.constraint(equalTo: document.leadingAnchor, constant: 16),
            stack.trailingAnchor.constraint(equalTo: document.trailingAnchor, constant: -16),
            stack.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -16),
        ])

        voicePopup = NSPopUpButton()
        voicePopup.target = self
        voicePopup.action = #selector(save)
        let preview = NSButton(title: "Preview", target: self, action: #selector(previewVoice))
        preview.bezelStyle = .rounded
        preview.setContentCompressionResistancePriority(.required, for: .horizontal)
        voicePopup.setContentHuggingPriority(.defaultLow, for: .horizontal)
        voicePopup.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let voiceRow = NSStackView(views: [voicePopup, preview])
        voiceRow.orientation = .horizontal
        voiceRow.alignment = .centerY
        voiceRow.spacing = 8
        stack.addArrangedSubview(labeled("Voice", voiceRow))

        speedSlider = NSSlider(value: 1, minValue: 0.25, maxValue: 4, target: self, action: #selector(speedMoved))
        speedSlider.translatesAutoresizingMaskIntoConstraints = false
        speedSlider.widthAnchor.constraint(equalToConstant: 220).isActive = true
        speedLabel = NSTextField(labelWithString: "1.0×")
        let speedRow = NSStackView(views: [speedSlider, speedLabel])
        stack.addArrangedSubview(labeled("Speed", speedRow))

        devicePopup = NSPopUpButton()
        devicePopup.target = self
        devicePopup.action = #selector(save)
        stack.addArrangedSubview(labeled("Output", devicePopup))

        speakCapture = HotkeyCaptureField(frame: .zero)
        modeCapture = HotkeyCaptureField(frame: .zero)
        speakCapture.onCapture = { [weak self] hotkey in self?.store.update { $0.speakHotkey = hotkey }; self?.persist() }
        modeCapture.onCapture = { [weak self] hotkey in self?.store.update { $0.modeHotkey = hotkey }; self?.persist() }
        stack.addArrangedSubview(labeled("Speak hotkey", speakCapture))
        stack.addArrangedSubview(labeled("Mode hotkey", modeCapture))

        loginCheck = checkbox("Launch at login", #selector(loginToggled))
        loginStatus = NSTextField(labelWithString: "")
        loginStatus.textColor = .secondaryLabelColor
        stack.addArrangedSubview(loginCheck)
        stack.addArrangedSubview(loginStatus)

        panelCheck = checkbox("Show the status panel", #selector(save))
        chimeCheck = checkbox("Play a sound when speech stops", #selector(save))
        stack.addArrangedSubview(panelCheck)
        stack.addArrangedSubview(chimeCheck)

        sentences = NSStepper()
        sentences.minValue = 1
        sentences.maxValue = 8
        sentences.target = self
        sentences.action = #selector(save)
        sentenceLabel = NSTextField(labelWithString: "3")
        audiencePopup = NSPopUpButton()
        audiencePopup.addItems(withTitles: ExplainAudience.allCases.map(\.title))
        audiencePopup.target = self
        audiencePopup.action = #selector(save)
        stack.addArrangedSubview(labeled("Explain sentences", NSStackView(views: [sentences, sentenceLabel])))
        stack.addArrangedSubview(labeled("Explain audience", audiencePopup))

        chatField = NSTextField()
        chatField.placeholderString = "Llama-3.2-1B-Instruct-GGUF"
        baseField = NSTextField()
        baseField.placeholderString = "http://127.0.0.1:13305"
        for field in [chatField!, baseField!] {
            field.translatesAutoresizingMaskIntoConstraints = false
            field.widthAnchor.constraint(equalToConstant: 280).isActive = true
            field.target = self
            field.action = #selector(save)
        }
        stack.addArrangedSubview(labeled("Chat model", chatField))
        stack.addArrangedSubview(labeled("Lemonade", baseField))

        let access = NSButton(title: "Grant Accessibility…", target: self, action: #selector(grantAccess))
        access.bezelStyle = .rounded
        stack.addArrangedSubview(access)
        let note = NSTextField(wrappingLabelWithString: "Accessibility lets the hotkey read a highlight. Without it, Dynamite falls back to copying the selection. The chat model is a small Metal Llama build. The Windows default Qwen is a Ryzen NPU model and does not run here.")
        note.textColor = .secondaryLabelColor
        note.preferredMaxLayoutWidth = 360
        note.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        stack.addArrangedSubview(note)
        note.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
    }

    private func reload() {
        let settings = store.current
        voicePopup.removeAllItems()
        for voice in VoiceCatalog.voices {
            voicePopup.addItem(withTitle: voice.name)
            voicePopup.lastItem?.representedObject = voice.id
        }
        if let index = VoiceCatalog.voices.firstIndex(where: { $0.id == settings.voice }) {
            voicePopup.selectItem(at: index)
        }
        speedSlider.doubleValue = settings.speed
        speedLabel.stringValue = String(format: "%.2f×", settings.speed)
        devices = AudioDevices.outputs()
        devicePopup.removeAllItems()
        devicePopup.addItem(withTitle: "System default")
        devicePopup.lastItem?.representedObject = ""
        for device in devices {
            devicePopup.addItem(withTitle: device.name)
            devicePopup.lastItem?.representedObject = device.uid
        }
        if let index = devices.firstIndex(where: { $0.uid == settings.audioDeviceUID }) {
            devicePopup.selectItem(at: index + 1)
        }
        speakCapture.stringValue = settings.speakHotkey.display
        modeCapture.stringValue = settings.modeHotkey.display
        loginCheck.state = settings.launchAtLogin ? .on : .off
        loginStatus.stringValue = LaunchAtLogin.statusText
        panelCheck.state = settings.showStatusPanel ? .on : .off
        chimeCheck.state = settings.playSoundOnStop ? .on : .off
        sentences.integerValue = settings.clampedExplainSentences
        sentenceLabel.stringValue = "\(settings.clampedExplainSentences)"
        audiencePopup.selectItem(at: ExplainAudience.allCases.firstIndex(of: settings.explainAudience) ?? 0)
        chatField.stringValue = settings.chatModel
        baseField.stringValue = settings.baseURL
    }

    @objc private func speedMoved() {
        speedLabel.stringValue = String(format: "%.2f×", speedSlider.doubleValue)
        save()
    }

    @objc private func loginToggled() {
        let enabled = loginCheck.state == .on
        do {
            try LaunchAtLogin.apply(enabled)
            store.update { $0.launchAtLogin = enabled }
            if SMAppService.mainApp.status == .requiresApproval {
                LaunchAtLogin.openSettings()
            }
        } catch {
            loginCheck.state = store.current.launchAtLogin ? .on : .off
            let alert = NSAlert()
            alert.messageText = "Launch at login"
            alert.informativeText = error.localizedDescription
            alert.beginSheetModal(for: window!)
        }
        loginStatus.stringValue = LaunchAtLogin.statusText
        onChange()
    }

    @objc private func save() {
        persist()
    }

    @objc private func previewVoice() {
        persist()
        let voice = voicePopup.titleOfSelectedItem ?? store.current.voice
        AppDelegate.shared?.speech.speak(
            text: "Hello. This is a preview of the \(voice) voice.",
            mode: .verbatim
        )
    }

    @objc private func grantAccess() {
        _ = SelectionReader.isTrusted(prompt: true)
        SelectionReader.openAccessibilitySettings()
    }

    private func persist() {
        store.update { settings in
            if let id = voicePopup.selectedItem?.representedObject as? String {
                settings.voice = id
            }
            settings.speed = speedSlider.doubleValue
            settings.audioDeviceUID = devicePopup.selectedItem?.representedObject as? String ?? ""
            settings.showStatusPanel = panelCheck.state == .on
            settings.playSoundOnStop = chimeCheck.state == .on
            settings.explainSentenceCount = sentences.integerValue
            settings.explainAudience = ExplainAudience.allCases[max(0, audiencePopup.indexOfSelectedItem)]
            let model = chatField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            if !model.isEmpty { settings.chatModel = model }
            let base = baseField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            if !base.isEmpty { settings.baseURL = base }
        }
        sentenceLabel.stringValue = "\(store.current.clampedExplainSentences)"
        speakCapture.stringValue = store.current.speakHotkey.display
        modeCapture.stringValue = store.current.modeHotkey.display
        onChange()
    }

    private func labeled(_ title: String, _ control: NSView) -> NSView {
        let label = NSTextField(labelWithString: title)
        label.font = .systemFont(ofSize: 12, weight: .semibold)
        let column = NSStackView(views: [label, control])
        column.orientation = .vertical
        column.alignment = .leading
        column.spacing = 4
        return column
    }

    private func checkbox(_ title: String, _ action: Selector) -> NSButton {
        let button = NSButton(checkboxWithTitle: title, target: self, action: action)
        return button
    }
}

private final class FlippedDocumentView: NSView {
    override var isFlipped: Bool { true }
}

final class HotkeyCaptureField: NSTextField {
    var onCapture: ((Hotkey) -> Void)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        isEditable = false
        isSelectable = true
        alignment = .center
        font = .monospacedSystemFont(ofSize: 13, weight: .regular)
        translatesAutoresizingMaskIntoConstraints = false
        widthAnchor.constraint(equalToConstant: 180).isActive = true
        placeholderString = "Click, then press a shortcut"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        stringValue = "Press shortcut"
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            window?.makeFirstResponder(nil)
            return
        }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let hotkey = Hotkey(
            keyCode: UInt32(event.keyCode),
            control: flags.contains(.control),
            option: flags.contains(.option),
            shift: flags.contains(.shift),
            command: flags.contains(.command)
        )
        guard hotkey.control || hotkey.option || hotkey.command else { return }
        stringValue = hotkey.display
        onCapture?(hotkey)
        window?.makeFirstResponder(nil)
    }
}
