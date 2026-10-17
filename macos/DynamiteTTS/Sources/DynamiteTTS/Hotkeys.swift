import AppKit
import Carbon
import Foundation

@MainActor
final class HotkeyCenter {
    static weak var shared: HotkeyCenter?

    var onSpeak: (() -> Void)?
    var onToggleMode: (() -> Void)?

    private var speakRef: EventHotKeyRef?
    private var modeRef: EventHotKeyRef?
    private var installed = false

    init() {
        HotkeyCenter.shared = self
    }

    func rebind(speak: Hotkey, mode: Hotkey) {
        installHandlerIfNeeded()
        if let speakRef { UnregisterEventHotKey(speakRef) }
        if let modeRef { UnregisterEventHotKey(modeRef) }
        self.speakRef = register(speak, id: 1)
        self.modeRef = register(mode, id: 2)
    }

    fileprivate func fired(id: UInt32) {
        if id == 1 { onSpeak?() }
        if id == 2 { onToggleMode?() }
    }

    private func installHandlerIfNeeded() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), hotKeyCallback, 1, &spec, nil, nil)
    }

    private func register(_ hotkey: Hotkey, id: UInt32) -> EventHotKeyRef? {
        var ref: EventHotKeyRef?
        let hotKeyID = EventHotKeyID(signature: OSType(0x44545453), id: id)
        let status = RegisterEventHotKey(
            hotkey.keyCode,
            hotkey.carbonModifiers,
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &ref
        )
        return status == noErr ? ref : nil
    }
}

private func hotKeyCallback(
    _ next: EventHandlerCallRef?,
    _ event: EventRef?,
    _ userData: UnsafeMutableRawPointer?
) -> OSStatus {
    var hotKeyID = EventHotKeyID()
    let status = GetEventParameter(
        event,
        EventParamName(kEventParamDirectObject),
        EventParamType(typeEventHotKeyID),
        nil,
        MemoryLayout<EventHotKeyID>.size,
        nil,
        &hotKeyID
    )
    if status == noErr {
        let id = hotKeyID.id
        let firedID = id
        Task { @MainActor in
            HotkeyCenter.shared?.fired(id: firedID)
        }
    }
    return noErr
}
