import Foundation

@MainActor
final class SpeechSession {
    let settings: SettingsStore
    let player = PCMPlayer()
    var onPhase: ((String?) -> Void)?
    var onNotice: ((String) -> Void)?

    private let client = LemonadeClient()
    private var stream: PCMStream?
    private var task: Task<Void, Never>?
    private var generation = 0
    private var streamFinished = false
    private var receivedAudio = false

    init(settings: SettingsStore) {
        self.settings = settings
        player.onDrained = { [weak self] in
            guard let self, self.streamFinished else { return }
            self.onPhase?(nil)
        }
    }

    func speak(text raw: String, context: String? = nil, mode: SpeakMode? = nil) {
        let text = TextCleanup.sanitize(raw)
        guard !text.isEmpty else {
            onNotice?("No text selected")
            return
        }
        stop(playChime: false)
        generation += 1
        let token = generation
        let activeMode = mode ?? settings.current.speakMode
        AppLog.write("speak chars=\(text.count) mode=\(activeMode.rawValue) voice=\(settings.current.voice)")
        task = Task { [weak self] in
            guard let self else { return }
            do {
                try await self.run(text: text, context: context, mode: activeMode, token: token)
            } catch is CancellationError {
                return
            } catch {
                AppLog.write("speak failed: \(error.localizedDescription)")
                if token == self.generation {
                    self.onNotice?(error.localizedDescription)
                    self.onPhase?(nil)
                }
            }
        }
    }

    func stop(playChime: Bool) {
        generation += 1
        task?.cancel()
        stream?.cancel()
        stream = nil
        player.stop(playChime: playChime && settings.current.playSoundOnStop && receivedAudio)
        receivedAudio = false
        streamFinished = false
        onPhase?(nil)
    }

    private func run(text: String, context: String?, mode: SpeakMode, token: Int) async throws {
        let settings = settings.current
        guard let baseURL = URL(string: settings.baseURL) else {
            throw LemonadeError.message("The Lemonade address is not a valid URL.")
        }
        onPhase?("Starting Lemonade…")
        try await client.ensureServer(baseURL: baseURL)
        AppLog.write("lemonade ready \(baseURL.absoluteString)")
        try Task.checkCancellation()

        var spoken = text
        if mode == .summary || mode == .explain {
            onPhase?(mode == .summary ? "Summarizing…" : "Explaining…")
            let model = await client.pickChatModel(baseURL: baseURL, preferred: settings.chatModel)
            try await client.ensureModel(baseURL: baseURL, model: model)
            try Task.checkCancellation()
            if mode == .summary {
                spoken = try await client.chat(baseURL: baseURL, model: model, system: Prompts.summary, user: text, maxTokens: nil)
            } else {
                let sentences = settings.clampedExplainSentences
                spoken = try await client.chat(
                    baseURL: baseURL,
                    model: model,
                    system: Prompts.explain(audience: settings.explainAudience, sentenceCount: sentences),
                    user: Prompts.explainUserMessage(selection: text, surrounding: context),
                    maxTokens: max(128, sentences * 96)
                )
            }
            spoken = TextCleanup.sanitize(spoken)
            if spoken.isEmpty { throw LemonadeError.message("The chat model returned nothing to speak.") }
        }

        try Task.checkCancellation()
        if token != generation { return }
        onPhase?("Synthesizing…")
        try await client.ensureModel(baseURL: baseURL, model: "kokoro-v1")
        AppLog.write("kokoro ready")
        try Task.checkCancellation()
        AppLog.write("player prepare device=\(settings.audioDeviceUID.isEmpty ? "default" : settings.audioDeviceUID)")
        try player.prepare(deviceUID: settings.audioDeviceUID)
        AppLog.write("player started")
        streamFinished = false
        receivedAudio = false

        let stream = PCMStream(
            onPCM: { [weak self] data in
                Task { @MainActor in
                    guard let self, token == self.generation else { return }
                    if !self.receivedAudio {
                        self.receivedAudio = true
                        AppLog.write("first audio bytes=\(data.count)")
                        self.onPhase?("Speaking")
                    }
                    self.player.enqueue(data)
                }
            },
            onComplete: { [weak self] error in
                Task { @MainActor in
                    guard let self, token == self.generation else { return }
                    self.streamFinished = true
                    if let error {
                        self.onNotice?(error.localizedDescription)
                        self.onPhase?(nil)
                    } else if !self.receivedAudio {
                        self.onNotice?("Lemonade returned no audio.")
                        self.onPhase?(nil)
                    } else if self.player.scheduledBuffers == 0 {
                        self.onPhase?(nil)
                    }
                }
            }
        )
        self.stream = stream
        AppLog.write("speech request chars=\(spoken.count) voice=\(settings.voice) speed=\(settings.speed)")
        try stream.start(baseURL: baseURL, text: spoken, voice: settings.voice, speed: settings.speed)
    }
}
