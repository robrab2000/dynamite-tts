import Foundation

@MainActor
final class SpeechSession {
    let settings: SettingsStore
    let player = PCMPlayer()
    /// The in-process Phonon engine; loaded the first time the Phonon engine is used.
    let phonon = PhononEngine()
    var onPhase: ((String?) -> Void)?
    var onNotice: ((String) -> Void)?

    private let client = LemonadeClient()
    private var stream: PCMStream?
    private var task: Task<Void, Never>?
    private var generation = 0
    private var streamFinished = false
    private var receivedAudio = false
    private var kokoroBackendInstalled = false

    init(settings: SettingsStore) {
        self.settings = settings
        player.onDrained = { [weak self] in
            guard let self, self.streamFinished else { return }
            self.onPhase?(nil)
        }
    }

    /// The engine in use: the launch-argument override, else the setting.
    var activeEngine: SpeechEngine { LaunchArguments.engineOverride ?? settings.current.engine }

    func speak(text raw: String, context: String? = nil, mode: SpeakMode? = nil, engine: SpeechEngine? = nil) {
        let text = TextCleanup.sanitize(raw)
        guard !text.isEmpty else {
            onNotice?("No text selected")
            return
        }
        stop(playChime: false)
        generation += 1
        let token = generation
        let activeMode = mode ?? settings.current.speakMode
        let engine = engine ?? activeEngine
        AppLog.write("speak chars=\(text.count) mode=\(activeMode.rawValue) engine=\(engine.rawValue) voice=\(engine == .phonon ? settings.current.phononVoice : settings.current.voice)")
        task = Task { [weak self] in
            guard let self else { return }
            do {
                try await self.run(text: text, context: context, mode: activeMode, engine: engine, token: token)
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
        phonon.cancel()
        player.stop(playChime: playChime && settings.current.playSoundOnStop && receivedAudio)
        receivedAudio = false
        streamFinished = false
        onPhase?(nil)
    }

    private func run(text: String, context: String?, mode: SpeakMode, engine: SpeechEngine, token: Int) async throws {
        let settings = settings.current
        let needsLemonade = engine == .kokoro || mode == .summary || mode == .explain
        var baseURL: URL?
        if needsLemonade {
            guard let url = URL(string: settings.baseURL) else {
                throw LemonadeError.message("The Lemonade address is not a valid URL.")
            }
            baseURL = url
            onPhase?("Starting Lemonade…")
            try await client.ensureServer(baseURL: url)
            AppLog.write("lemonade ready \(url.absoluteString)")
            try Task.checkCancellation()
        }

        var spoken = text
        if mode == .summary || mode == .explain, let baseURL {
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
        switch engine {
        case .phonon:
            try await speakWithPhonon(spoken, settings: settings, token: token)
        case .kokoro:
            guard let baseURL else { return }
            try await speakWithKokoro(spoken, baseURL: baseURL, settings: settings, token: token)
        }
    }

    private func startPlayer(_ settings: AppSettings) throws {
        AppLog.write("player prepare device=\(settings.audioDeviceUID.isEmpty ? "default" : settings.audioDeviceUID)")
        try player.prepare(deviceUID: settings.audioDeviceUID)
        AppLog.write("player started")
        streamFinished = false
        receivedAudio = false
    }

    // MARK: - Phonon (in-process)

    private func speakWithPhonon(_ spoken: String, settings: AppSettings, token: Int) async throws {
        if !phonon.isLoaded { onPhase?("Loading Phonon…") }
        phonon.onStatus = { [weak self] status in
            Task { @MainActor in
                guard let self, token == self.generation, !self.receivedAudio else { return }
                if case .ready = self.phonon.state { return }
                self.onPhase?(status)
            }
        }
        try await phonon.prepare(customModelDir: settings.phononModelDir, language: settings.phononLanguage)
        try Task.checkCancellation()
        if token != generation { return }
        onPhase?("Synthesizing…")
        try startPlayer(settings)
        AppLog.write("phonon request chars=\(spoken.count) voice=\(settings.phononVoice) speed=\(settings.speed) lang=\(settings.phononLanguage)")
        let requested = Date()
        let stats = try await phonon.speak(text: spoken, voice: settings.phononVoice, speed: settings.speed) { [weak self] samples in
            Task { @MainActor in
                guard let self, token == self.generation else { return }
                if !self.receivedAudio {
                    self.receivedAudio = true
                    let ms = Int(Date().timeIntervalSince(requested) * 1000)
                    AppLog.write("first audio samples=\(samples.count) after \(ms) ms")
                    self.onPhase?("Speaking")
                }
                self.player.enqueue(samples: samples)
            }
        }
        guard token == generation else { return }
        streamFinished = true
        if stats.samples == 0 {
            onNotice?("Phonon produced no audio.")
            onPhase?(nil)
        } else if player.scheduledBuffers == 0 {
            onPhase?(nil)
        }
    }

    // MARK: - Kokoro (Lemonade)

    private func speakWithKokoro(_ spoken: String, baseURL: URL, settings: AppSettings, token: Int) async throws {
        // The installer leaves Kokoro out; its backend and voice (about 340 MB) arrive the first time it is used.
        if !kokoroBackendInstalled {
            onPhase?("Installing Kokoro…")
            try await client.installBackend(baseURL: baseURL, recipe: "kokoro", backend: "metal")
            kokoroBackendInstalled = true
            try Task.checkCancellation()
        }
        onPhase?("Synthesizing…")
        try await client.ensureModel(baseURL: baseURL, model: "kokoro-v1") { [weak self] in
            AppLog.write("kokoro-v1 not downloaded; pulling")
            Task { @MainActor in
                guard let self, token == self.generation else { return }
                self.onPhase?("Downloading Kokoro (once)…")
            }
        }
        AppLog.write("kokoro ready")
        try Task.checkCancellation()
        try startPlayer(settings)

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
