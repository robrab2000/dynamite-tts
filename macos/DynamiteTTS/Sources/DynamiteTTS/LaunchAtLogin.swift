import AppKit
import Foundation
import ServiceManagement

enum LaunchAtLogin {
    static var isBundledApp: Bool {
        Bundle.main.bundleURL.pathExtension == "app"
    }

    static var statusText: String {
        guard isBundledApp else { return "Available after you open DynamiteTTS.app." }
        switch SMAppService.mainApp.status {
        case .enabled: return "On"
        case .requiresApproval: return "Waiting for approval in System Settings"
        case .notRegistered, .notFound: return "Off"
        @unknown default: return "Off"
        }
    }

    static func apply(_ enabled: Bool) throws {
        guard isBundledApp else {
            throw LemonadeError.message("Launch at login works from the signed DynamiteTTS.app, not from swift run.")
        }
        let service = SMAppService.mainApp
        if enabled {
            if service.status != .enabled {
                try service.register()
            }
        } else if service.status != .notRegistered {
            try service.unregister()
        }
    }

    static func openSettings() {
        let urls = [
            "x-apple.systempreferences:com.apple.LoginItems-Settings.extension",
            "x-apple.systempreferences:com.apple.settings.LoginItems",
        ]
        for raw in urls {
            if let url = URL(string: raw), NSWorkspace.shared.open(url) { return }
        }
    }
}
