import Foundation
import XCTest
@testable import DynamiteTTS

/// End-to-end checks of `PhononEngine` against the real shim and model, without an audio device.
/// Needs `cargo build --release` in windows/native/phonon (or DYNAMITE_PHONON_LIB) and the model in
/// Application Support (macos/install-phonon-model.sh). Prints the measurements and writes
/// macos/Spike/phonon-default-voice.wav (or DYNAMITE_PHONON_WAV) with the default voice.
final class PhononEngineTests: XCTestCase {
    static let short = "The quick brown fox jumps over the lazy dog near the river bank."
    static let paragraph = "Phonon is a small text to speech model that streams audio as it generates it. "
        + "On an Apple Silicon laptop the whole paragraph renders many times faster than realtime, "
        + "so playback can begin after the first few frames while the rest is still being produced. "
        + "This paragraph exists to measure how long a longer passage takes from the first word to the last, "
        + "and whether the speaking rate stays steady throughout."

    private static let repoRoot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()

    private final class Collector: @unchecked Sendable {
        private let lock = NSLock()
        private let started = Date()
        private var firstBlockAt: Double?
        private(set) var samples: [Float] = []
        var onFirstBlock: (@Sendable () -> Void)?

        func add(_ block: [Float]) {
            let first: Bool = lock.withLock {
                let first = firstBlockAt == nil
                if first { firstBlockAt = Date().timeIntervalSince(started) * 1000 }
                samples += block
                return first
            }
            if first { onFirstBlock?() }
        }

        var firstBlockMs: Double { lock.withLock { firstBlockAt ?? -1 } }
        var seconds: Double { lock.withLock { Double(samples.count) / Double(PhononEngine.sampleRate) } }
    }

    private func makeEngine() async throws -> (PhononEngine, Double) {
        if ProcessInfo.processInfo.environment["DYNAMITE_PHONON_LIB"] == nil {
            let built = Self.repoRoot.appendingPathComponent("windows/native/phonon/target/release/\(PhononEngine.libraryFileName)")
            setenv("DYNAMITE_PHONON_LIB", built.path, 1)
        }
        guard PhononEngine.isRuntimePresent else { throw XCTSkip("libdynamite_phonon.dylib is not built") }
        guard try PhononEngine.resolveModelDirectory(customDir: nil) != nil else {
            throw XCTSkip("phonon-31m-en is not downloaded; run macos/install-phonon-model.sh")
        }
        let engine = PhononEngine()
        let started = Date()
        try await engine.prepare(customModelDir: nil, language: "en")
        return (engine, Date().timeIntervalSince(started) * 1000)
    }

    private func speak(_ engine: PhononEngine, _ text: String, voice: String? = nil) async throws -> (PhononEngine.SpeakStats, Collector) {
        let collector = Collector()
        let stats = try await engine.speak(text: text, voice: voice, speed: 1.0) { collector.add($0) }
        let realtime = stats.totalMs > 0 ? collector.seconds / (stats.totalMs / 1000) : 0
        print(String(
            format: "phonon %@ %d chars: %.2f s audio in %d blocks, first audio %.1f ms (Swift side %.1f ms), total %.0f ms, %.1fx realtime",
            voice ?? "default", text.count, collector.seconds, stats.blocks, stats.firstAudioMs,
            collector.firstBlockMs, stats.totalMs, realtime
        ))
        return (stats, collector)
    }

    func testLoadSpeakAndMeasure() async throws {
        let (engine, loadMs) = try await makeEngine()
        print(String(format: "phonon load (incl. priming) %.0f ms, %d threads, runtime %@", loadMs, engine.threads, engine.runtimeVersion ?? "?"))

        let (shortStats, shortAudio) = try await speak(engine, Self.short)
        XCTAssertGreaterThan(shortAudio.seconds, 1.5)
        XCTAssertLessThan(shortStats.firstAudioMs, 200)

        let (paraStats, paraAudio) = try await speak(engine, Self.paragraph)
        XCTAssertGreaterThan(paraAudio.seconds, 15)
        XCTAssertLessThan(paraStats.totalMs / 1000, paraAudio.seconds, "slower than realtime")

        let wav = ProcessInfo.processInfo.environment["DYNAMITE_PHONON_WAV"].map(URL.init(fileURLWithPath:))
            ?? Self.repoRoot.appendingPathComponent("macos/Spike/phonon-default-voice.wav")
        try Self.writeWav(paraAudio.samples, to: wav)
        print("phonon wrote \(wav.path)")
    }

    func testCancelStopsGeneration() async throws {
        let (engine, _) = try await makeEngine()
        let collector = Collector()
        let cancelledAt = LockedDate()
        collector.onFirstBlock = {
            cancelledAt.set(Date())
            engine.cancel()
        }
        var stopped = false
        do {
            _ = try await engine.speak(text: Self.paragraph, voice: nil, speed: 1.0) { collector.add($0) }
        } catch is CancellationError {
            stopped = true
        }
        let returnMs = cancelledAt.get().map { Date().timeIntervalSince($0) * 1000 } ?? -1
        print(String(format: "phonon cancel after first block: returned %.1f ms after cancel with %.2f s of audio (full paragraph ~22 s)", returnMs, collector.seconds))
        XCTAssertTrue(stopped)
        XCTAssertLessThan(collector.seconds, 5)

        let (_, after) = try await speak(engine, Self.short)
        XCTAssertGreaterThan(after.seconds, 1.5, "the engine speaks again after a cancel")
    }

    func testCatalogueVoice() async throws {
        let (engine, _) = try await makeEngine()
        let (_, audio) = try await speak(engine, Self.short, voice: "Easton")
        XCTAssertTrue(engine.loadedVoices.contains("Easton"))
        XCTAssertGreaterThan(audio.seconds, 1.5)
    }

    func testSpeedMapping() {
        XCTAssertEqual(PhononEngine.durationDelta(forSpeed: 1.0), 0)
        XCTAssertEqual(PhononEngine.durationDelta(forSpeed: 4.0), -0.8)
        XCTAssertEqual(PhononEngine.durationDelta(forSpeed: 0.25), 0.8)
        XCTAssertEqual(Double(PhononEngine.durationDelta(forSpeed: 1.2)), -2 * log(1.2), accuracy: 1e-5)
    }

    private static func writeWav(_ samples: [Float], to url: URL) throws {
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) } }
        let rate = UInt32(PhononEngine.sampleRate)
        data.append(contentsOf: Array("RIFF".utf8)); append(UInt32(36 + samples.count * 2))
        data.append(contentsOf: Array("WAVEfmt ".utf8)); append(UInt32(16)); append(UInt16(1)); append(UInt16(1))
        append(rate); append(rate * 2); append(UInt16(2)); append(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); append(UInt32(samples.count * 2))
        for sample in samples { append(Int16(max(-1, min(1, sample)) * 32767)) }
        try data.write(to: url)
    }
}

private final class LockedDate: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Date?
    func set(_ date: Date) { lock.withLock { value = date } }
    func get() -> Date? { lock.withLock { value } }
}
