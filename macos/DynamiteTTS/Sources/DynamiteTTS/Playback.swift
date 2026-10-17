import AVFAudio
import CoreAudio
import Foundation

struct AudioOutputDevice: Equatable {
    var uid: String
    var name: String
}

enum AudioDevices {
    static func outputs() -> [AudioOutputDevice] {
        deviceIDs().compactMap { device in
            guard hasOutput(device) else { return nil }
            let uid = stringProperty(device, kAudioDevicePropertyDeviceUID) ?? ""
            let name = stringProperty(device, kAudioDevicePropertyDeviceNameCFString) ?? "Output"
            guard !uid.isEmpty else { return nil }
            return AudioOutputDevice(uid: uid, name: name)
        }
    }

    static func deviceID(forUID uid: String) -> AudioDeviceID? {
        guard !uid.isEmpty else { return nil }
        for device in deviceIDs() where hasOutput(device) {
            if stringProperty(device, kAudioDevicePropertyDeviceUID) == uid {
                return device
            }
        }
        return nil
    }

    private static func deviceIDs() -> [AudioDeviceID] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        let system = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return [] }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var ids = Array(repeating: AudioDeviceID(), count: count)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    private static func hasOutput(_ device: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreams,
            mScope: kAudioDevicePropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr else { return false }
        return size > 0
    }

    private static func stringProperty(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size = UInt32(MemoryLayout<CFString?>.size)
        var value: Unmanaged<CFString>?
        let status = withUnsafeMutablePointer(to: &value) { pointer in
            AudioObjectGetPropertyData(device, &address, 0, nil, &size, pointer)
        }
        guard status == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }
}

@MainActor
final class PCMPlayer {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 24_000, channels: 1, interleaved: false)!
    private var started = false
    private var playGeneration = 0
    private var heldBuffers: [HeldBuffer] = []
    private var queuedFrames: AVAudioFramePosition = 0
    private var loggedClock = false
    private(set) var level: Float = 0
    private(set) var scheduledBuffers = 0
    var onDrained: (() -> Void)?
    private var drainTimer: Timer?
    nonisolated(unsafe) private static weak var active: PCMPlayer?

    private struct HeldBuffer {
        let buffer: AVAudioPCMBuffer
        let generation: Int
        let countsAsSpeech: Bool
        let endFrame: AVAudioFramePosition
    }

    func prepare(deviceUID: String) throws {
        if started {
            player.stop()
            engine.stop()
            started = false
        }
        if engine.attachedNodes.contains(player) == false {
            engine.attach(player)
        }
        engine.disconnectNodeOutput(player)
        engine.connect(player, to: engine.mainMixerNode, format: format)
        if let deviceID = AudioDevices.deviceID(forUID: deviceUID), let unit = engine.outputNode.audioUnit {
            var device = deviceID
            AudioUnitSetProperty(
                unit,
                kAudioOutputUnitProperty_CurrentDevice,
                kAudioUnitScope_Global,
                0,
                &device,
                UInt32(MemoryLayout<AudioDeviceID>.size)
            )
        }
        try engine.start()
        player.play()
        started = true
        heldBuffers.removeAll()
        scheduledBuffers = 0
        queuedFrames = 0
        loggedClock = false
        Self.active = self
        drainTimer?.invalidate()
        drainTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { _ in
            Task { @MainActor in
                Self.active?.drainFinished()
            }
        }
    }

    func enqueue(_ pcm16: Data) {
        guard started else { return }
        let framesPerBuffer = 4_800
        var offset = 0
        while offset + 1 < pcm16.count {
            let frames = min(framesPerBuffer, (pcm16.count - offset) / 2)
            guard frames > 0 else { return }
            let byteCount = frames * 2
            schedule(pcm16.subdata(in: offset..<(offset + byteCount)), countsAsSpeech: true)
            offset += byteCount
        }
    }

    func stop(playChime: Bool) {
        playGeneration += 1
        player.stop()
        heldBuffers.removeAll()
        queuedFrames = 0
        loggedClock = false
        scheduledBuffers = 0
        level = 0
        guard playChime, started, let buffer = chimeBuffer() else { return }
        player.play()
        schedule(buffer)
    }

    func shutdown() {
        player.stop()
        engine.stop()
        started = false
    }

    private func schedule(_ pcm16: Data, countsAsSpeech: Bool) {
        let frames = pcm16.count / 2
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
              let channel = buffer.floatChannelData?[0] else { return }
        buffer.frameLength = AVAudioFrameCount(frames)
        pcm16.withUnsafeBytes { raw in
            var peak: Float = 0
            for index in 0..<frames {
                let sample = Float(raw.loadUnaligned(fromByteOffset: index * 2, as: Int16.self)) / 32768
                channel[index] = sample
                peak = max(peak, abs(sample))
            }
            level = max(level, peak)
        }
        schedule(buffer, countsAsSpeech: countsAsSpeech)
    }

    private func schedule(_ buffer: AVAudioPCMBuffer, countsAsSpeech: Bool = false) {
        let generation = playGeneration
        queuedFrames += AVAudioFramePosition(buffer.frameLength)
        heldBuffers.append(HeldBuffer(
            buffer: buffer,
            generation: generation,
            countsAsSpeech: countsAsSpeech,
            endFrame: queuedFrames
        ))
        if countsAsSpeech { scheduledBuffers += 1 }
        Self.active = self
        AppLog.write("schedule frames=\(buffer.frameLength) held=\(heldBuffers.count)")
        player.scheduleBuffer(buffer)
        AppLog.write("schedule returned queued=\(queuedFrames)")
    }

    private func drainFinished() {
        guard started,
              let renderTime = player.lastRenderTime,
              let playerTime = player.playerTime(forNodeTime: renderTime) else { return }
        let played = playerTime.sampleTime
        if !loggedClock {
            loggedClock = true
            AppLog.write("render sample=\(played) queued=\(queuedFrames)")
        }
        let generation = playGeneration
        var finishedSpeech = 0
        heldBuffers.removeAll { held in
            guard played >= held.endFrame else { return false }
            if held.generation == generation, held.countsAsSpeech { finishedSpeech += 1 }
            return true
        }
        guard finishedSpeech > 0 else { return }
        scheduledBuffers = max(0, scheduledBuffers - finishedSpeech)
        level *= 0.4
        if scheduledBuffers == 0 {
            AppLog.write("playback drained")
            onDrained?()
        }
    }

    private func chimeBuffer() -> AVAudioPCMBuffer? {
        let frames = Int(format.sampleRate * 0.09)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
              let channel = buffer.floatChannelData?[0] else { return nil }
        buffer.frameLength = AVAudioFrameCount(frames)
        for index in 0..<frames {
            let envelope = Float(frames - index) / Float(frames)
            let phase = Float(index) / Float(format.sampleRate) * 880 * 2 * .pi
            channel[index] = sin(phase) * envelope * 0.2
        }
        return buffer
    }
}
