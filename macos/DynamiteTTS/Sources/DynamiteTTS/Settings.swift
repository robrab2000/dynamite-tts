import Carbon
import Foundation

enum SpeakMode: String, Codable, CaseIterable {
    case verbatim
    case summary
    case explain

    var title: String {
        switch self {
        case .verbatim: "Verbatim"
        case .summary: "Summary"
        case .explain: "Explain"
        }
    }

    func next() -> SpeakMode {
        switch self {
        case .verbatim: .summary
        case .summary: .explain
        case .explain: .verbatim
        }
    }
}

enum ExplainAudience: String, Codable, CaseIterable {
    case technical
    case general

    var title: String {
        switch self {
        case .technical: "Technical"
        case .general: "General"
        }
    }
}

struct Hotkey: Codable, Equatable {
    var keyCode: UInt32
    var control: Bool
    var option: Bool
    var shift: Bool
    var command: Bool

    static let speak = Hotkey(keyCode: 0x01, control: true, option: false, shift: true, command: false)
    static let mode = Hotkey(keyCode: 0x2E, control: true, option: false, shift: true, command: false)

    var carbonModifiers: UInt32 {
        var bits: UInt32 = 0
        if command { bits |= UInt32(cmdKey) }
        if shift { bits |= UInt32(shiftKey) }
        if option { bits |= UInt32(optionKey) }
        if control { bits |= UInt32(controlKey) }
        return bits
    }

    var display: String {
        var parts: [String] = []
        if control { parts.append("Ctrl") }
        if option { parts.append("Opt") }
        if shift { parts.append("Shift") }
        if command { parts.append("Cmd") }
        parts.append(Hotkey.keyName(keyCode))
        return parts.joined(separator: "+")
    }

    static func keyName(_ keyCode: UInt32) -> String {
        let names: [UInt32: String] = [
            0x00: "A", 0x01: "S", 0x02: "D", 0x03: "F", 0x04: "H", 0x05: "G", 0x06: "Z", 0x07: "X",
            0x08: "C", 0x09: "V", 0x0B: "B", 0x0C: "Q", 0x0D: "W", 0x0E: "E", 0x0F: "R", 0x10: "Y",
            0x11: "T", 0x12: "1", 0x13: "2", 0x14: "3", 0x15: "4", 0x16: "6", 0x17: "5", 0x18: "=",
            0x19: "9", 0x1A: "7", 0x1B: "-", 0x1C: "8", 0x1D: "0", 0x1E: "]", 0x1F: "O", 0x20: "U",
            0x21: "[", 0x22: "I", 0x23: "P", 0x25: "L", 0x26: "J", 0x27: "'", 0x28: "K", 0x29: ";", 0x2A: "\\",
            0x2B: ",", 0x2C: "/", 0x2D: "N", 0x2E: "M", 0x2F: ".", 0x31: "Space", 0x24: "Return",
            0x30: "Tab", 0x33: "Delete", 0x35: "Esc",
        ]
        return names[keyCode] ?? "Key\(keyCode)"
    }
}

struct AppSettings: Codable, Equatable {
    var baseURL: String = "http://127.0.0.1:13305"
    var voice: String = "af_heart"
    var speed: Double = 1.0
    var speakMode: SpeakMode = .verbatim
    var speakHotkey: Hotkey = .speak
    var modeHotkey: Hotkey = .mode
    var audioDeviceUID: String = ""
    var launchAtLogin: Bool = false
    var showStatusPanel: Bool = true
    var playSoundOnStop: Bool = true
    var explainSentenceCount: Int = 3
    var explainAudience: ExplainAudience = .technical
    var chatModel: String = "Llama-3.2-1B-Instruct-GGUF"

    var clampedExplainSentences: Int { min(8, max(1, explainSentenceCount)) }
}

enum VoiceCatalog {
    static let voices: [(id: String, name: String)] = [
        ("af_heart", "af_heart · US female, warm"),
        ("af_sky", "af_sky · US female, clear"),
        ("af_bella", "af_bella · US female, expressive"),
        ("af_sarah", "af_sarah · US female, smooth"),
        ("af_nicole", "af_nicole · US female, crisp"),
        ("am_adam", "am_adam · US male, narrative"),
        ("am_michael", "am_michael · US male, professional"),
        ("am_echo", "am_echo · US male, balanced"),
        ("bf_emma", "bf_emma · UK female"),
        ("bf_alice", "bf_alice · UK female, articulate"),
        ("bm_george", "bm_george · UK male"),
        ("bm_daniel", "bm_daniel · UK male, conversational"),
        ("coral", "coral · OpenAI alias"),
    ]
}

@MainActor
final class SettingsStore {
    nonisolated static let supportDirectory: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("DynamiteTTS", isDirectory: true)
    }()

    private let fileURL: URL
    private(set) var current: AppSettings

    init() {
        fileURL = Self.supportDirectory.appendingPathComponent("settings.json")
        current = Self.load(from: fileURL)
    }

    func update(_ mutate: (inout AppSettings) -> Void) {
        mutate(&current)
        current.explainSentenceCount = current.clampedExplainSentences
        current.speed = min(4, max(0.25, current.speed))
        save()
    }

    private func save() {
        try? FileManager.default.createDirectory(at: Self.supportDirectory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        if let data = try? encoder.encode(current) {
            try? data.write(to: fileURL, options: .atomic)
        }
    }

    private static func load(from url: URL) -> AppSettings {
        guard let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder().decode(AppSettings.self, from: data) else {
            return AppSettings()
        }
        return decoded
    }
}
