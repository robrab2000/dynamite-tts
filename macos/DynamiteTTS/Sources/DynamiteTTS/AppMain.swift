import AppKit
import Darwin

@main
enum DynamiteMain {
    static func main() {
        if !SingleInstance.claim() {
            return
        }

        let app = NSApplication.shared
        let delegate = AppDelegate()
        AppDelegate.shared = delegate
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}

enum SingleInstance {
    private final class Hold: @unchecked Sendable {
        let handle: FileHandle
        init(_ handle: FileHandle) { self.handle = handle }
    }

    nonisolated(unsafe) private static var hold: Hold?

    static func claim() -> Bool {
        let directory = SettingsStore.supportDirectory
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("instance.lock")
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try? FileHandle(forUpdating: url)
        guard let handle else { return true }
        if flock(handle.fileDescriptor, LOCK_EX | LOCK_NB) != 0 {
            return false
        }
        hold = Hold(handle)
        return true
    }
}
