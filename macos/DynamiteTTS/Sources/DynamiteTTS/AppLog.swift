import Darwin
import Foundation

enum AppLog {
    static let fileURL: URL = {
        let directory = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/DynamiteTTS", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory.appendingPathComponent("dynamite.log")
    }()

    nonisolated(unsafe) static var fileDescriptor: Int32 = -1
    private static let lock = NSLock()

    static func open() {
        lock.lock()
        defer { lock.unlock() }
        if fileDescriptor >= 0 { return }
        let path = fileURL.path
        fileDescriptor = path.withCString { Darwin.open($0, O_WRONLY | O_CREAT | O_APPEND, 0o644) }
        installCrashHandlers()
        let version = ProcessInfo.processInfo.operatingSystemVersionString
        let pathToApp = Bundle.main.bundlePath
        store("launch pid=\(getpid()) os=\(version) bundle=\(pathToApp)")
    }

    static func write(_ message: String) {
        let stamp = ISO8601DateFormatter().string(from: Date())
        let line = "\(stamp) \(message)"
        print(line)
        fflush(stdout)
        lock.lock()
        defer { lock.unlock() }
        store(line)
    }

    private static func store(_ line: String) {
        guard fileDescriptor >= 0 else { return }
        var text = line
        if !text.hasSuffix("\n") { text += "\n" }
        text.withCString { pointer in
            _ = Darwin.write(fileDescriptor, pointer, strlen(pointer))
        }
        _ = fsync(fileDescriptor)
    }

    private static func installCrashHandlers() {
        NSSetUncaughtExceptionHandler { exception in
            let reason = exception.reason ?? ""
            let stack = exception.callStackSymbols.joined(separator: " | ")
            AppLog.write("CRASH exception \(exception.name.rawValue): \(reason) \(stack)")
        }
        signal(SIGABRT, crashAbort)
        signal(SIGSEGV, crashSegv)
        signal(SIGBUS, crashBus)
        signal(SIGILL, crashIll)
        signal(SIGTRAP, crashTrap)
    }
}

private func crashAbort(_: Int32) { crashNote("CRASH SIGABRT\n"); reraise(SIGABRT) }
private func crashSegv(_: Int32) { crashNote("CRASH SIGSEGV\n"); reraise(SIGSEGV) }
private func crashBus(_: Int32) { crashNote("CRASH SIGBUS\n"); reraise(SIGBUS) }
private func crashIll(_: Int32) { crashNote("CRASH SIGILL\n"); reraise(SIGILL) }
private func crashTrap(_: Int32) { crashNote("CRASH SIGTRAP\n"); reraise(SIGTRAP) }

private func crashNote(_ text: StaticString) {
    let fd = AppLog.fileDescriptor
    guard fd >= 0 else { return }
    text.withUTF8Buffer { buffer in
        guard let base = buffer.baseAddress else { return }
        _ = Darwin.write(fd, base, buffer.count)
    }
    _ = fsync(fd)
}

private func reraise(_ sig: Int32) {
    signal(sig, SIG_DFL)
    raise(sig)
}
