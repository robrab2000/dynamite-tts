import Foundation

enum LemonadeError: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let text): text
        }
    }
}

struct LemonadeLocation {
    var lemond: URL?
    var lemonade: URL?

    static func discover() -> LemonadeLocation {
        var location = LemonadeLocation()
        let support = SettingsStore.supportDirectory.appendingPathComponent("lemonade")
        let candidates = [
            support.appendingPathComponent("lemond"),
            URL(fileURLWithPath: "/opt/homebrew/bin/lemond"),
            URL(fileURLWithPath: "/usr/local/bin/lemond"),
            URL(fileURLWithPath: "/Applications/Lemonade.app/Contents/MacOS/lemond"),
        ]
        location.lemond = candidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
        let cliCandidates = [
            support.appendingPathComponent("lemonade"),
            URL(fileURLWithPath: "/opt/homebrew/bin/lemonade"),
            URL(fileURLWithPath: "/usr/local/bin/lemonade"),
        ]
        location.lemonade = cliCandidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
        if location.lemond == nil, let found = which("lemond") { location.lemond = found }
        if location.lemonade == nil, let found = which("lemonade") { location.lemonade = found }
        return location
    }

    private static func which(_ name: String) -> URL? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/which")
        process.arguments = [name]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        try? process.run()
        process.waitUntilExit()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        guard process.terminationStatus == 0,
              let path = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              !path.isEmpty else { return nil }
        return URL(fileURLWithPath: path)
    }
}

final class LemonadeClient: @unchecked Sendable {
    private let session: URLSession
    private var serverProcess: Process?

    init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 180
        configuration.timeoutIntervalForResource = 600
        session = URLSession(configuration: configuration)
    }

    func ensureServer(baseURL: URL) async throws {
        if await isHealthy(baseURL) { return }
        let location = LemonadeLocation.discover()
        guard let lemond = location.lemond else {
            throw LemonadeError.message(
                "Lemonade is not installed. Run macos/install-lemonade.sh, then open Dynamite TTS again."
            )
        }
        let port = baseURL.port ?? 13305
        let process = Process()
        process.executableURL = lemond
        process.arguments = ["--host", "127.0.0.1", "--port", "\(port)", "--no-broadcast"]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        serverProcess = process
        let deadline = Date().addingTimeInterval(25)
        while Date() < deadline {
            if await isHealthy(baseURL) { return }
            try await Task.sleep(nanoseconds: 250_000_000)
        }
        throw LemonadeError.message("Lemonade did not start on \(baseURL.host ?? "127.0.0.1"):\(port).")
    }

    func ensureModel(baseURL: URL, model: String) async throws {
        var request = URLRequest(url: lemonadeEndpoint(baseURL, "/api/v1/load"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["model_name": model])
        let (data, response) = try await session.data(for: request)
        if let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) {
            return
        }
        let body = String(data: data, encoding: .utf8) ?? ""
        if body.contains("not found") || body.contains("unknown_model") || body.contains("not downloaded") {
            try await pull(baseURL: baseURL, model: model)
            let (retryData, retryResponse) = try await session.data(for: request)
            if let http = retryResponse as? HTTPURLResponse, (200..<300).contains(http.statusCode) {
                return
            }
            let retryBody = String(data: retryData, encoding: .utf8) ?? ""
            throw LemonadeError.message(shorten(retryBody))
        }
        throw LemonadeError.message(shorten(body))
    }

    func pull(baseURL: URL, model: String) async throws {
        var request = URLRequest(url: lemonadeEndpoint(baseURL, "/api/v1/pull"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 600
        request.httpBody = try JSONSerialization.data(withJSONObject: ["model_name": model])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw LemonadeError.message(shorten(String(data: data, encoding: .utf8) ?? "Could not download \(model)."))
        }
    }

    func chat(baseURL: URL, model: String, system: String, user: String, maxTokens: Int?) async throws -> String {
        var payload: [String: Any] = [
            "model": model,
            "messages": [
                ["role": "system", "content": system],
                ["role": "user", "content": user],
            ],
            "stream": false,
        ]
        if let maxTokens, maxTokens > 0 {
            payload["max_tokens"] = maxTokens
        }
        var request = URLRequest(url: lemonadeEndpoint(baseURL, "/api/v1/chat/completions"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: payload)
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw LemonadeError.message(shorten(String(data: data, encoding: .utf8) ?? "Chat request failed."))
        }
        return try parseChat(data)
    }

    func pickChatModel(baseURL: URL, preferred: String) async -> String {
        guard let url = URL(string: baseURL.absoluteString + "/api/v1/models"),
              let (data, _) = try? await session.data(from: url),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = root["data"] as? [[String: Any]] else {
            return preferred
        }
        let ids = rows.compactMap { $0["id"] as? String }
        if ids.contains(where: { $0.caseInsensitiveCompare(preferred) == .orderedSame }) {
            return preferred
        }
        let chat = ids.first { id in
            let lower = id.lowercased()
            return !["kokoro", "tts", "whisper", "speech", "audio", "moss", "stt", "asr"].contains { lower.contains($0) }
        }
        return chat ?? preferred
    }

    private func isHealthy(_ baseURL: URL) async -> Bool {
        guard let url = URL(string: baseURL.absoluteString + "/api/v1/health") else { return false }
        var request = URLRequest(url: url)
        request.timeoutInterval = 2
        guard let (_, response) = try? await session.data(for: request),
              let http = response as? HTTPURLResponse else { return false }
        return (200..<300).contains(http.statusCode)
    }

    private func parseChat(_ data: Data) throws -> String {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choices = root["choices"] as? [[String: Any]],
              let message = choices.first?["message"] as? [String: Any] else {
            throw LemonadeError.message("Lemonade returned a chat response without text.")
        }
        if let content = message["content"] as? String {
            let trimmed = content.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.isEmpty { throw LemonadeError.message("The chat model returned an empty reply.") }
            return trimmed
        }
        throw LemonadeError.message("The chat model returned a reply Dynamite could not read.")
    }

    private func shorten(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.count <= 240 { return trimmed.isEmpty ? "Lemonade request failed." : trimmed }
        return String(trimmed.prefix(240)) + "…"
    }
}

private func lemonadeEndpoint(_ base: URL, _ path: String) -> URL {
    var root = base.absoluteString
    while root.hasSuffix("/") { root.removeLast() }
    return URL(string: root + path)!
}

final class PCMStream: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var leftover = Data()
    private let onPCM: (Data) -> Void
    private let onComplete: (Error?) -> Void

    init(onPCM: @escaping (Data) -> Void, onComplete: @escaping (Error?) -> Void) {
        self.onPCM = onPCM
        self.onComplete = onComplete
    }

    func start(baseURL: URL, text: String, voice: String, speed: Double) throws {
        var request = URLRequest(url: lemonadeEndpoint(baseURL, "/api/v1/audio/speech"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.timeoutInterval = 180
        let payload: [String: Any] = [
            "model": "kokoro-v1",
            "input": text,
            "voice": voice,
            "speed": speed,
            "stream_format": "audio",
            "response_format": "pcm",
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: payload)
        let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
        self.session = session
        let task = session.dataTask(with: request)
        self.task = task
        task.resume()
    }

    func cancel() {
        task?.cancel()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        leftover.append(data)
        let even = leftover.count - (leftover.count % 2)
        guard even > 0 else { return }
        let chunk = leftover.prefix(even)
        leftover.removeFirst(even)
        onPCM(Data(chunk))
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let http = task.response as? HTTPURLResponse, !(200..<300).contains(http.statusCode), error == nil {
            onComplete(LemonadeError.message("Speech request failed (\(http.statusCode))."))
            return
        }
        if let error = error as NSError?, error.code == NSURLErrorCancelled {
            onComplete(nil)
            return
        }
        onComplete(error)
    }
}
