import AppKit
import ApplicationServices
import Foundation

@MainActor
enum SelectionReader {
    static func isTrusted(prompt: Bool) -> Bool {
        guard prompt else { return AXIsProcessTrusted() }
        let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }

    static func openAccessibilitySettings() {
        let urls = [
            "x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_Accessibility",
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility",
        ]
        for raw in urls {
            if let url = URL(string: raw), NSWorkspace.shared.open(url) { return }
        }
    }

    static func capture(includeContext: Bool) -> (text: String?, context: String?) {
        if let focused = focusedElement() {
            let selected = stringAttribute(focused, kAXSelectedTextAttribute)
            let value = includeContext ? stringAttribute(focused, kAXValueAttribute) : nil
            if let selected, !selected.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return (selected, value)
            }
        }
        return (pasteboardCopy(), nil)
    }

    private static func focusedElement() -> AXUIElement? {
        let system = AXUIElementCreateSystemWide()
        return elementAttribute(system, kAXFocusedUIElementAttribute)
    }

    private static func elementAttribute(_ element: AXUIElement, _ name: String) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success, let value else { return nil }
        return (value as! AXUIElement)
    }

    private static func stringAttribute(_ element: AXUIElement, _ name: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func pasteboardCopy() -> String? {
        let pasteboard = NSPasteboard.general
        let previous = snapshot(pasteboard)
        let changeCount = pasteboard.changeCount
        postCommandC()
        let deadline = Date().addingTimeInterval(0.8)
        var text: String?
        while Date() < deadline {
            if pasteboard.changeCount != changeCount {
                text = pasteboard.string(forType: .string)
                break
            }
            Thread.sleep(forTimeInterval: 0.01)
        }
        if let previous {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
                restore(previous, to: pasteboard)
            }
        }
        let trimmed = text?.trimmingCharacters(in: .whitespacesAndNewlines)
        return (trimmed?.isEmpty == false) ? trimmed : nil
    }

    private static func snapshot(_ pasteboard: NSPasteboard) -> [NSPasteboardItem]? {
        pasteboard.pasteboardItems?.map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) {
                    copy.setData(data, forType: type)
                }
            }
            return copy
        }
    }

    private static func restore(_ items: [NSPasteboardItem], to pasteboard: NSPasteboard) {
        pasteboard.clearContents()
        pasteboard.writeObjects(items)
    }

    private static func postCommandC() {
        let source = CGEventSource(stateID: .hidSystemState)
        for key in [UInt16(0x38), 0x3C, 0x3B, 0x3E, 0x3A, 0x3D] {
            CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: false)?.post(tap: .cghidEventTap)
        }
        let down = CGEvent(keyboardEventSource: source, virtualKey: 0x08, keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: 0x08, keyDown: false)
        down?.flags = .maskCommand
        up?.flags = .maskCommand
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }
}
