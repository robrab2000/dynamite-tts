import CDynamitePhonon
import CryptoKit
import Darwin
import Foundation

enum PhononError: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let text): text
        }
    }
}

/// What the engine is doing, for the settings window and the status pill.
enum PhononEngineState: Equatable {
    case notInitialized
    /// libdynamite_phonon.dylib is not in the app bundle (a build without the Rust shim).
    case runtimeMissing
    case downloading(Double)
    case loading
    case ready
    case failed
}

/// In-process speech through Gradium's Phonon (github.com/gradium-ai/xn-ptts) via the Rust shim in
/// `windows/native/phonon`, built for arm64 as `libdynamite_phonon.dylib`. Phonon is a 31M/40M
/// flow-matching model that streams audio as it generates it, so the first block is ready within
/// about ten milliseconds and a paragraph renders about thirty times faster than realtime on an
/// M1 Max (measured: load 30 ms, first audio 7-10 ms, 28-34x realtime on 4-6 threads).
///
/// The shim is loaded with dlopen, so the app builds and runs without it; the model is bundled
/// in the .app (`Contents/Resources/models/phonon-31m-en`) or downloaded once into
/// `~/Library/Application Support/DynamiteTTS/models`, pinned and SHA-256 verified; catalogue voices
/// (250 KB each) are fetched on first use. The same pins as the Windows and Linux apps.
///
/// Every call into the shim runs on one serial queue, so a load, a voice registration and an
/// utterance never overlap. `dp_speak` blocks that queue for the whole utterance and delivers
/// audio through a C callback; cancellation flips a flag the callback reads, which makes the
/// shim return within one frame.
final class PhononEngine: @unchecked Sendable {
    static let sampleRate = 24_000
    static let defaultModelId = "phonon-31m-en"
    static let libraryFileName = "libdynamite_phonon.dylib"

    /// The commit the ptts 0.6.0 runtime itself pins for its default model.
    static let modelRevision = "8c64b55ec301728e2d1695bfea87a08a267d0c2d"
    private static let modelRepo = "https://huggingface.co/gradium/phonon-31m-en/resolve/\(modelRevision)/"
    /// The commit the runtime pins for its voice catalogue; `PhononVoiceCatalog` is this snapshot.
    static let voiceCatalogRevision = "da6c9ec0d47be8df5c87c730a3aa97187f3229c3"
    private static let voiceRepo = "https://huggingface.co/gradium/phonon-voices/resolve/\(voiceCatalogRevision)/"

    struct ModelFile: Sendable {
        let name: String
        let size: Int
        let sha256: String
    }

    /// The checkpoint files the shim needs, with the hashes of the pinned revision
    /// (same table as macos/install-phonon-model.sh and the Windows PhononTtsService).
    static let modelFiles: [ModelFile] = [
        ModelFile(name: "config.json", size: 2_851, sha256: "7062ea376e6b158543c8abec8e5af67df171c49ad70eb7e2a0fb25c16f265e06"),
        ModelFile(name: "tokenizer.json", size: 277_967, sha256: "691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936"),
        ModelFile(name: "model.q8.gguf", size: 64_512_352, sha256: "a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae"),
        ModelFile(name: "default-voice.safetensors", size: 256_136, sha256: "f739e8c4c76ee7f92e7c94a82002748c8cd22e99582ac49f7b9b94cd63786e18"),
    ]

    static var totalDownloadBytes: Int { modelFiles.reduce(0) { $0 + $1.size } }

    nonisolated static var modelsRoot: URL {
        SettingsStore.supportDirectory.appendingPathComponent("models", isDirectory: true)
    }

    nonisolated static var modelCacheDirectory: URL { modelsRoot.appendingPathComponent(defaultModelId, isDirectory: true) }
    nonisolated static var voiceCacheDirectory: URL { modelsRoot.appendingPathComponent("phonon-voices", isDirectory: true) }

    /// Maximum magnitude of the duration_delta condition; larger values make speech unreliable.
    static let maxDurationDelta = 0.8

    /// Maps the app's speed multiplier onto Phonon's duration_delta condition (negative = faster).
    /// Measured on phonon-31m-en: -0.6 → 1.32x, -0.3 → 1.19x, +0.5 → 0.82x, which fits
    /// speed ≈ exp(-0.5·delta). Clamped to ±`maxDurationDelta`, i.e. about 0.67x-1.5x.
    static func durationDelta(forSpeed speed: Double) -> Float {
        guard speed.isFinite, speed > 0 else { return 0 }
        let delta = -2.0 * log(speed)
        return Float(min(maxDurationDelta, max(-maxDurationDelta, delta)))
    }

    /// CPU threads for the engine. Phonon is one autoregressive stream that saturates at a few
    /// threads; efficiency cores slow it down. Measured on a 10-core M1 Max (8P+2E): 2 threads 17x,
    /// 4 threads 25x, 6-8 threads 29x, all 10 threads 14x realtime. So: the performance cores, at
    /// most six.
    static let defaultThreads: Int = {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        if sysctlbyname("hw.perflevel0.physicalcpu", &value, &size, nil, 0) == 0, value > 0 {
            return min(6, max(2, Int(value)))
        }
        return min(6, max(2, ProcessInfo.processInfo.activeProcessorCount / 2))
    }()

    // MARK: - Runtime (the dylib)

    private typealias NewFn = @convention(c) (UnsafePointer<CChar>?, UnsafePointer<CChar>?, UInt32) -> OpaquePointer?
    private typealias SpeakFn = @convention(c) (
        OpaquePointer?, UnsafePointer<CChar>?, UnsafePointer<CChar>?, Float, DpFrameFn?,
        UnsafeMutableRawPointer?, UnsafeMutablePointer<DpResult>?
    ) -> Int32
    private typealias AddVoiceFn = @convention(c) (OpaquePointer?, UnsafePointer<CChar>?, UnsafePointer<CChar>?) -> Int32
    private typealias VoicesFn = @convention(c) (OpaquePointer?) -> UnsafePointer<CChar>?
    private typealias SetLanguageFn = @convention(c) (OpaquePointer?, UnsafePointer<CChar>?) -> Int32
    private typealias SetDeltaFn = @convention(c) (OpaquePointer?, Float) -> Int32
    private typealias SampleRateFn = @convention(c) (OpaquePointer?) -> UInt32
    private typealias LastErrorFn = @convention(c) (OpaquePointer?) -> UnsafePointer<CChar>?
    private typealias FreeFn = @convention(c) (OpaquePointer?) -> Void
    private typealias VersionFn = @convention(c) () -> UnsafePointer<CChar>?

    private struct Runtime {
        let path: String
        let new: NewFn
        let speak: SpeakFn
        let addVoice: AddVoiceFn
        let voices: VoicesFn
        let setLanguage: SetLanguageFn
        let setDurationDelta: SetDeltaFn
        let sampleRate: SampleRateFn
        let lastError: LastErrorFn
        let free: FreeFn
        let version: VersionFn

        init(path: String) throws {
            guard let dl = dlopen(path, RTLD_NOW | RTLD_LOCAL) else {
                let reason = dlerror().map { String(cString: $0) } ?? "unknown"
                throw PhononError.message("Could not load \(PhononEngine.libraryFileName): \(reason)")
            }
            func symbol<T>(_ name: String, as type: T.Type) throws -> T {
                guard let p = dlsym(dl, name) else {
                    throw PhononError.message("\(PhononEngine.libraryFileName) has no \(name); rebuild the shim from windows/native/phonon.")
                }
                return unsafeBitCast(p, to: type)
            }
            self.path = path
            new = try symbol("dp_new", as: NewFn.self)
            speak = try symbol("dp_speak", as: SpeakFn.self)
            addVoice = try symbol("dp_add_voice", as: AddVoiceFn.self)
            voices = try symbol("dp_voices", as: VoicesFn.self)
            setLanguage = try symbol("dp_set_language", as: SetLanguageFn.self)
            setDurationDelta = try symbol("dp_set_duration_delta", as: SetDeltaFn.self)
            sampleRate = try symbol("dp_sample_rate", as: SampleRateFn.self)
            lastError = try symbol("dp_last_error", as: LastErrorFn.self)
            free = try symbol("dp_free", as: FreeFn.self)
            version = try symbol("dp_runtime_version", as: VersionFn.self)
        }

        func error(_ handle: OpaquePointer?) -> String {
            lastError(handle).map { String(cString: $0) } ?? "unknown error"
        }
    }

    /// Where the shim may sit: `DYNAMITE_PHONON_LIB`, the app bundle's Frameworks folder, next to
    /// the executable (a `swift build` tree), the per-user support folder, and the cargo output
    /// folder of a checkout that contains this build (for `swift run` during development).
    nonisolated static func libraryCandidates() -> [URL] {
        var candidates: [URL] = []
        if let override = ProcessInfo.processInfo.environment["DYNAMITE_PHONON_LIB"], !override.isEmpty {
            candidates.append(URL(fileURLWithPath: override))
        }
        if let frameworks = Bundle.main.privateFrameworksURL {
            candidates.append(frameworks.appendingPathComponent(libraryFileName))
        }
        let executable = Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0])
        let executableDir = executable.deletingLastPathComponent()
        candidates.append(executableDir.appendingPathComponent(libraryFileName))
        candidates.append(SettingsStore.supportDirectory.appendingPathComponent("lib/\(libraryFileName)"))
        // .../macos/DynamiteTTS/.build/<config>/DynamiteTTS → .../windows/native/phonon/target/release
        var probe = executableDir
        for _ in 0..<6 {
            probe = probe.deletingLastPathComponent()
            let cargoOut = probe.appendingPathComponent("windows/native/phonon/target/release/\(libraryFileName)")
            if FileManager.default.fileExists(atPath: cargoOut.path) {
                candidates.append(cargoOut)
                break
            }
        }
        return candidates
    }

    nonisolated static func locateLibrary() -> URL? {
        libraryCandidates().first { FileManager.default.fileExists(atPath: $0.path) }
    }

    nonisolated static var isRuntimePresent: Bool { locateLibrary() != nil }

    // MARK: - State

    private let lock = NSLock()
    /// Every dp_* call happens here, one at a time.
    private let queue = DispatchQueue(label: "com.robrab2000.dynamitett.phonon", qos: .userInteractive)
    private var runtime: Runtime?
    private var handle: OpaquePointer?
    private var modelDir = ""
    private var language = ""
    private var durationDelta: Float?
    private var registeredVoices: Set<String> = []
    private var activeContext: SpeakContext?
    private var prepareTask: Task<Void, Error>?
    private(set) var state: PhononEngineState = .notInitialized
    private(set) var stateDescription = "Not initialized"
    var threads = PhononEngine.defaultThreads
    /// Progress and state text, on whatever thread changed it.
    var onStatus: (@Sendable (String) -> Void)?

    var isLoaded: Bool { lock.withLock { handle != nil } }
    var loadedModelDirectory: String { lock.withLock { modelDir } }
    var runtimeVersion: String? {
        lock.withLock { runtime.flatMap { $0.version().map { String(cString: $0) } } }
    }

    private func setState(_ state: PhononEngineState, _ description: String) {
        let changed: Bool = lock.withLock {
            let changed = self.state != state
            if !changed && stateDescription == description { return false }
            self.state = state
            stateDescription = description
            return true
        }
        guard changed || true else { return }
        if changed { AppLog.write("phonon: \(state): \(description)") }
        onStatus?(description)
    }

    // MARK: - Model files

    /// The checkpoint folder to load: `customDir` when it holds a checkpoint, otherwise the copy in
    /// the app bundle or the per-user cache. Nil when the default model has not been downloaded yet.
    static func resolveModelDirectory(customDir: String?) throws -> URL? {
        let custom = (customDir ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if !custom.isEmpty {
            let url = URL(fileURLWithPath: (custom as NSString).expandingTildeInPath)
            if isCheckpoint(url) { return url }
            throw PhononError.message("The Phonon model folder '\(custom)' has no config.json, tokenizer.json and weights (model.q8.gguf or model.safetensors).")
        }
        if let bundled = Bundle.main.resourceURL?.appendingPathComponent("models/\(defaultModelId)", isDirectory: true),
           isDefaultCheckpointComplete(bundled) {
            return bundled
        }
        return isDefaultCheckpointComplete(modelCacheDirectory) ? modelCacheDirectory : nil
    }

    static func isCheckpoint(_ dir: URL) -> Bool {
        let fm = FileManager.default
        return fm.fileExists(atPath: dir.appendingPathComponent("config.json").path)
            && fm.fileExists(atPath: dir.appendingPathComponent("tokenizer.json").path)
            && (fm.fileExists(atPath: dir.appendingPathComponent("model.q8.gguf").path)
                || fm.fileExists(atPath: dir.appendingPathComponent("model.safetensors").path))
    }

    private static func isDefaultCheckpointComplete(_ dir: URL) -> Bool {
        modelFiles.allSatisfy { file in
            fileSize(dir.appendingPathComponent(file.name)) == file.size
        }
    }

    private static func fileSize(_ url: URL) -> Int? {
        (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.intValue
    }

    private static func sha256Matches(_ url: URL, _ expected: String) -> Bool {
        guard let data = try? Data(contentsOf: url, options: .mappedIfSafe) else { return false }
        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        return digest.caseInsensitiveCompare(expected) == .orderedSame
    }

    /// Downloads the default checkpoint into the cache (sha256-verified), skipping files already there.
    func ensureDefaultModel() async throws -> URL {
        let dir = Self.modelCacheDirectory
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let total = Self.totalDownloadBytes
        var done = 0
        for file in Self.modelFiles {
            let target = dir.appendingPathComponent(file.name)
            if Self.fileSize(target) == file.size, Self.sha256Matches(target, file.sha256) {
                done += file.size
                reportDownload(done, of: total)
                continue
            }
            let base = done
            try await Self.download(
                from: Self.modelRepo + file.name, to: target, expectedSize: file.size, sha256: file.sha256
            ) { [weak self] received in self?.reportDownload(base + received, of: total) }
            done += file.size
            reportDownload(done, of: total)
        }
        return dir
    }

    private func reportDownload(_ bytes: Int, of total: Int) {
        let fraction = min(1, max(0, Double(bytes) / Double(max(1, total))))
        let percent = Int((fraction * 100).rounded())
        setState(.downloading(fraction), "Downloading the Phonon model (\(percent)% of \(total / 1_000_000) MB)…")
    }

    /// The local file of a catalogue voice, downloading it (once) when missing.
    func ensureVoiceFile(_ voice: PhononVoice) async throws -> URL {
        let dir = Self.voiceCacheDirectory
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let target = dir.appendingPathComponent("\(voice.voiceId).safetensors")
        if let size = Self.fileSize(target), size > 1_000 { return target }
        AppLog.write("phonon: downloading voice \(voice.name)")
        try await Self.download(from: Self.voiceRepo + voice.fileName, to: target, expectedSize: nil, sha256: nil) { _ in }
        return target
    }

    private static func download(
        from urlString: String, to target: URL, expectedSize: Int?, sha256: String?,
        progress: @escaping @Sendable (Int) -> Void
    ) async throws {
        guard let url = URL(string: urlString) else { throw PhononError.message("Bad download URL \(urlString)") }
        var request = URLRequest(url: url)
        request.setValue("DynamiteTTS/1.0", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 1_800
        let temporary = try await ProgressDownloader().download(request, progress: progress)
        defer { try? FileManager.default.removeItem(at: temporary) }
        if let expectedSize, fileSize(temporary) != expectedSize {
            throw PhononError.message("\(target.lastPathComponent) downloaded with the wrong size.")
        }
        if let sha256, !sha256Matches(temporary, sha256) {
            throw PhononError.message("\(target.lastPathComponent) failed its checksum.")
        }
        _ = try? FileManager.default.removeItem(at: target)
        try FileManager.default.moveItem(at: temporary, to: target)
    }

    // MARK: - Lifecycle

    static func normalizeLanguage(_ language: String?) -> String {
        let lang = (language ?? "").trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return lang.isEmpty ? "en" : lang
    }

    /// Loads the engine for the settings (downloading the default model first when it is not on
    /// disk). Joins an in-flight load; returns at once when the same model and language are loaded.
    func prepare(customModelDir: String?, language rawLanguage: String) async throws {
        let language = Self.normalizeLanguage(rawLanguage)
        let existing: Task<Void, Error>? = lock.withLock {
            if let prepareTask { return prepareTask }
            if handle != nil, sameModel(customModelDir), self.language == language { return nil }
            let task = Task { [self] in
                defer { lock.withLock { prepareTask = nil } }
                try await load(customModelDir: customModelDir, language: language)
            }
            prepareTask = task
            return task
        }
        if let existing { try await existing.value }
    }

    private func sameModel(_ customModelDir: String?) -> Bool {
        guard let wanted = try? Self.resolveModelDirectory(customDir: customModelDir) else { return false }
        return wanted.standardizedFileURL.path == URL(fileURLWithPath: modelDir).standardizedFileURL.path
    }

    private func load(customModelDir: String?, language: String) async throws {
        let runtime: Runtime
        do {
            runtime = try lock.withLock {
                if let runtime = self.runtime { return runtime }
                guard let url = Self.locateLibrary() else {
                    throw PhononError.message(
                        "\(Self.libraryFileName) is missing from the app. Build it with `cargo build --release` in windows/native/phonon and run macos/package-app.sh again."
                    )
                }
                let loaded = try Runtime(path: url.path)
                self.runtime = loaded
                return loaded
            }
        } catch {
            setState(.runtimeMissing, error.localizedDescription)
            throw error
        }

        var dir: URL?
        do {
            dir = try Self.resolveModelDirectory(customDir: customModelDir)
        } catch {
            setState(.failed, error.localizedDescription)
            throw error
        }
        if dir == nil {
            setState(.downloading(0), "Downloading the Phonon model (\(Self.totalDownloadBytes / 1_000_000) MB)…")
            do {
                dir = try await ensureDefaultModel()
            } catch {
                setState(.failed, "Phonon model download failed: \(Self.shorten(error.localizedDescription))")
                throw error
            }
        }
        guard let dir else { throw PhononError.message("No Phonon model folder.") }

        setState(.loading, "Loading Phonon…")
        let started = Date()
        let threads = UInt32(max(1, self.threads))
        try await onQueue { [self] in
            let opened = dir.path.withCString { d in language.withCString { l in runtime.new(d, l, threads) } }
            guard let opened else {
                let error = runtime.error(nil)
                setState(.failed, "Phonon failed to load: \(Self.shorten(error))")
                throw PhononError.message("Phonon failed to load \(dir.path): \(error)")
            }
            let old: OpaquePointer? = lock.withLock {
                let old = handle
                handle = opened
                modelDir = dir.path
                self.language = language
                durationDelta = nil
                registeredVoices = Set(Self.voiceNames(runtime.voices(opened)))
                return old
            }
            if let old { runtime.free(old) }
            // Prime the model so the first real request pays no first-call cost.
            _ = "Ready.".withCString { text in
                runtime.speak(opened, text, nil, -1, { _, _, _ in 1 }, nil, nil)
            }
        }
        let version = runtime.version().map { String(cString: $0) } ?? "?"
        let ms = Int(Date().timeIntervalSince(started) * 1000)
        AppLog.write("phonon: loaded \(dir.lastPathComponent) (runtime \(version), \(threads) threads, lang \(language), \(runtime.path)) in \(ms) ms")
        setState(.ready, "\(dir.lastPathComponent) on the CPU (\(threads) threads, language \(language))")
    }

    private static func voiceNames(_ blob: UnsafePointer<CChar>?) -> [String] {
        guard var p = blob else { return [] }
        var names: [String] = []
        while p.pointee != 0 {
            let name = String(cString: p)
            names.append(name)
            p += name.utf8.count + 1
        }
        return names
    }

    private func onQueue<T: Sendable>(_ body: @escaping @Sendable () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result { try body() })
            }
        }
    }

    // MARK: - Voices

    /// Voices the loaded engine can speak right now (checkpoint voices plus registered catalogue voices).
    var loadedVoices: [String] { lock.withLock { Array(registeredVoices).sorted() } }

    /// The voice name to pass to the engine: nil for the checkpoint's default, a catalogue voice
    /// (downloaded and registered on first use), or a voice the loaded checkpoint itself ships.
    func resolveVoice(_ voiceName: String?) async throws -> String? {
        let requested = (voiceName ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if requested.isEmpty || requested.caseInsensitiveCompare(PhononVoice.defaultId) == .orderedSame { return nil }
        if let known = lock.withLock({ registeredVoices.first { $0.caseInsensitiveCompare(requested) == .orderedSame } }) {
            return known
        }
        guard let voice = PhononVoiceCatalog.find(requested), !voice.isDefault else {
            AppLog.write("phonon: unknown voice '\(requested)', using the model's default voice")
            return nil
        }
        if let known = lock.withLock({ registeredVoices.first { $0 == voice.name } }) { return known }
        let file: URL
        do {
            file = try await ensureVoiceFile(voice)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            AppLog.write("phonon: could not download voice \(voice.name): \(error.localizedDescription); using the default voice")
            return nil
        }
        return try await onQueue { [self] in
            guard let (runtime, handle) = current() else { return nil }
            let ok = voice.name.withCString { n in file.path.withCString { p in runtime.addVoice(handle, n, p) } }
            guard ok != 0 else {
                AppLog.write("phonon: registering voice \(voice.name) failed: \(runtime.error(handle))")
                return nil
            }
            lock.withLock { _ = registeredVoices.insert(voice.name) }
            return voice.name
        }
    }

    private func current() -> (Runtime, OpaquePointer)? {
        lock.withLock {
            guard let runtime, let handle else { return nil }
            return (runtime, handle)
        }
    }

    // MARK: - Synthesis

    struct SpeakStats: Sendable {
        var samples: Int
        var blocks: Int
        var firstAudioMs: Double
        var totalMs: Double
        var cancelled: Bool
    }

    private final class SpeakContext: @unchecked Sendable {
        let onBlock: @Sendable ([Float]) -> Void
        let lock = NSLock()
        var cancelled = false
        var blocks = 0
        var samples = 0

        init(onBlock: @escaping @Sendable ([Float]) -> Void) { self.onBlock = onBlock }

        var isCancelled: Bool { lock.withLock { cancelled } }
        func cancel() { lock.withLock { cancelled = true } }
    }

    /// Runs on the thread inside dp_speak. Copies the block out and hands it on; a cancelled
    /// context returns 0, which stops generation. Must never throw across the native frame.
    private static let frameCallback: DpFrameFn = { pcm, count, user in
        guard let user else { return 1 }
        let context = Unmanaged<SpeakContext>.fromOpaque(user).takeUnretainedValue()
        if context.isCancelled { return 0 }
        guard let pcm, count > 0 else { return 1 }
        let block = Array(UnsafeBufferPointer(start: pcm, count: count))
        context.lock.withLock {
            context.blocks += 1
            context.samples += count
        }
        context.onBlock(block)
        return context.isCancelled ? 0 : 1
    }

    /// Speaks `text` (the shim's runtime splits it into sentences itself), handing every decoded
    /// block of 24 kHz mono float samples to `onBlock` as soon as the engine produces it. Returns
    /// when the utterance is spoken or `cancel()` stopped it; call `prepare` first.
    func speak(
        text: String, voice: String?, speed: Double, onBlock: @escaping @Sendable ([Float]) -> Void
    ) async throws -> SpeakStats {
        let voiceName = try await resolveVoice(voice)
        let delta = Self.durationDelta(forSpeed: speed)
        let context = SpeakContext(onBlock: onBlock)
        lock.withLock { activeContext = context }
        defer { lock.withLock { if activeContext === context { activeContext = nil } } }
        if Task.isCancelled { throw CancellationError() }
        return try await onQueue { [self] in
            guard let (runtime, handle) = current() else { throw PhononError.message("The Phonon engine is not loaded.") }
            if context.isCancelled { throw CancellationError() }
            let currentDelta = lock.withLock { durationDelta }
            if currentDelta == nil || abs(currentDelta! - delta) > 1e-4 {
                if runtime.setDurationDelta(handle, delta) == 0 {
                    AppLog.write("phonon: duration_delta \(delta) rejected: \(runtime.error(handle))")
                } else {
                    lock.withLock { durationDelta = delta }
                }
            }
            var result = DpResult()
            let user = Unmanaged.passUnretained(context).toOpaque()
            let ok = text.withCString { t -> Int32 in
                if let voiceName {
                    return voiceName.withCString { v in runtime.speak(handle, t, v, -1, Self.frameCallback, user, &result) }
                }
                return runtime.speak(handle, t, nil, -1, Self.frameCallback, user, &result)
            }
            if context.isCancelled { throw CancellationError() }
            guard ok != 0 else { throw PhononError.message("Phonon synthesis failed: \(runtime.error(handle))") }
            let stats = SpeakStats(
                samples: Int(result.samples), blocks: context.blocks, firstAudioMs: result.ttfa_ms,
                totalMs: result.total_ms, cancelled: false
            )
            let seconds = Double(stats.samples) / Double(Self.sampleRate)
            AppLog.write(String(
                format: "phonon: %d chars -> %.1fs audio in %d blocks, first audio %.0f ms, total %.0f ms (%.1fx realtime)",
                text.count, seconds, stats.blocks, stats.firstAudioMs, stats.totalMs,
                stats.totalMs > 0 ? seconds / (stats.totalMs / 1000) : 0
            ))
            return stats
        }
    }

    /// Stops the utterance in progress, if any; dp_speak returns within one frame.
    func cancel() {
        lock.withLock { activeContext }?.cancel()
    }

    private static func shorten(_ message: String) -> String {
        let line = message.split(separator: "\n").first.map(String.init) ?? message
        return line.count > 160 ? String(line.prefix(160)) + "…" : line
    }

    deinit {
        if let runtime, let handle { runtime.free(handle) }
    }
}

/// A URLSession download with byte progress, as one async call.
private final class ProgressDownloader: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private var continuation: CheckedContinuation<URL, Error>?
    private var progress: (@Sendable (Int) -> Void)?
    private var session: URLSession?

    func download(_ request: URLRequest, progress: @escaping @Sendable (Int) -> Void) async throws -> URL {
        self.progress = progress
        let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
        self.session = session
        let task = session.downloadTask(with: request)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                self.continuation = continuation
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        progress?(Int(totalBytesWritten))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // The file is deleted when this returns; keep it.
        let kept = FileManager.default.temporaryDirectory.appendingPathComponent("dynamite-\(UUID().uuidString).download")
        do {
            if let http = downloadTask.response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                finish(.failure(PhononError.message("Download failed (\(http.statusCode)) for \(downloadTask.originalRequest?.url?.lastPathComponent ?? "file").")))
                return
            }
            try FileManager.default.moveItem(at: location, to: kept)
            finish(.success(kept))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error {
            let cancelled = (error as NSError).code == NSURLErrorCancelled
            finish(.failure(cancelled ? CancellationError() : error))
        }
        session.finishTasksAndInvalidate()
    }

    private func finish(_ result: Result<URL, Error>) {
        guard let continuation else { return }
        self.continuation = nil
        continuation.resume(with: result)
    }
}
