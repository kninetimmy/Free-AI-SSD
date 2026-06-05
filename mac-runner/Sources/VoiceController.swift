import Foundation
import AVFoundation
import Speech

// MARK: - Native macOS voice (task #94)
//
// Per decision #166 the Mac runner backs voice with the native macOS frameworks
// (AVSpeechSynthesizer for TTS, Speech.framework / SFSpeechRecognizer for STT)
// rather than hosting Piper/Whisper through the .NET sidecar. This file is the
// framework glue; the framework-free chunking/citation/remap logic it leans on
// lives in VoiceTextProcessing.swift (which the unit-test binary links instead).
//
// This file is intentionally excluded from the swiftc test targets — it imports
// AVFoundation + Speech, which the standalone pure-logic test binaries don't link.

// MARK: - Text to speech

/// A selectable system voice, surfaced in the SwiftUI voice picker. `id` is the
/// stable AVSpeechSynthesisVoice identifier persisted as `ttsVoiceName`.
struct TtsVoiceOption: Identifiable, Hashable {
    let id: String
    let label: String
    /// Premium/Enhanced neural voices — the "Siri-grade" voices the user
    /// downloads under System Settings → Accessibility → Spoken Content. These
    /// get a ⭐ in the picker and sort above the robotic bundled "compact" ones.
    let isHighQuality: Bool
}

/// Audio-quality tier of an AVSpeechSynthesisVoice.
///
/// We read `AVSpeechSynthesisVoiceQuality.rawValue` (1 = default, 2 = enhanced,
/// 3 = premium) instead of the `.premium` symbol on purpose: `.premium` is
/// macOS 13+, and this app targets the macOS 11 baseline (see MAC1), so naming
/// it directly wouldn't compile without availability guards. The raw values are
/// ABI-stable, so the comparison is safe across SDKs and deployment targets.
private func ttsVoiceTier(_ quality: AVSpeechSynthesisVoiceQuality) -> (rank: Int, label: String, highQuality: Bool) {
    switch quality.rawValue {
    case 3: return (0, "Premium", true)
    case 2: return (1, "Enhanced", true)
    default: return (2, "Default", false)
    }
}

/// Wraps AVSpeechSynthesizer to speak streamed chat responses sentence-by-
/// sentence (so audio starts before generation finishes) and one-off test text.
/// Mirrors the Windows `ITextToSpeechService` surface the runner needs.
final class MacTextToSpeech {
    private let synthesizer = AVSpeechSynthesizer()

    /// AVSpeechSynthesisVoice identifier (stable, locale-independent). Persisted
    /// as `ttsVoiceName` in PortableConfig. nil → system default voice.
    private var voiceIdentifier: String?
    /// PortableConfig encodings (cross-OS): rate -10…10 (0 neutral), volume 0…100.
    private var configRate: Int = 0
    private var configVolume: Int = 100

    /// Per-response chunker; non-nil only while a chat stream is being spoken.
    private var chunker: SentenceChunker?

    var isSpeaking: Bool { synthesizer.isSpeaking }

    /// Installed voices, best-first. Ordering:
    ///   1. English before other languages (the DCS/aviation default), then
    ///   2. higher audio quality (Premium → Enhanced → Default), then
    ///   3. alphabetical by name.
    /// The label carries the tier ("Ava (en-US) · Premium") and Premium/Enhanced
    /// voices get a ⭐ so the user can tell the good neural voices from the
    /// robotic compact ones — Apple walls off the literal Siri voice from
    /// AVSpeechSynthesizer, so these downloadable neural voices are the closest
    /// we can offer.
    static func availableVoices() -> [TtsVoiceOption] {
        return AVSpeechSynthesisVoice.speechVoices()
            .map { v in (voice: v, en: v.language.hasPrefix("en"), tier: ttsVoiceTier(v.quality)) }
            .sorted { a, b in
                if a.en != b.en { return a.en && !b.en }
                if a.tier.rank != b.tier.rank { return a.tier.rank < b.tier.rank }
                return a.voice.name.localizedCaseInsensitiveCompare(b.voice.name) == .orderedAscending
            }
            .map { entry in
                let star = entry.tier.highQuality ? "⭐ " : ""
                return TtsVoiceOption(
                    id: entry.voice.identifier,
                    label: "\(star)\(entry.voice.name) (\(entry.voice.language)) · \(entry.tier.label)",
                    isHighQuality: entry.tier.highQuality)
            }
    }

    /// Best installed voice to land on for a brand-new config: the top-ranked
    /// English neural (Premium/Enhanced) voice, or nil when none is installed —
    /// in which case the caller falls back to the system default and the UI
    /// nudges the user to download better voices.
    static func bestDefaultVoiceIdentifier() -> String? {
        return availableVoices().first(where: { $0.isHighQuality })?.id
    }

    func configure(voiceIdentifier: String?, rate: Int, volume: Int) {
        self.voiceIdentifier = (voiceIdentifier?.isEmpty == true) ? nil : voiceIdentifier
        self.configRate = rate
        self.configVolume = volume
    }

    // MARK: streaming

    /// Begin speaking a fresh response. Clears any prior in-flight speech so a
    /// new chat doesn't talk over the last one.
    func beginStreaming() {
        stop()
        chunker = SentenceChunker()
    }

    func feed(_ token: String) {
        guard let sentence = chunker?.feed(token) else { return }
        enqueue(sentence)
    }

    func finishStreaming() {
        if let tail = chunker?.finish() { enqueue(tail) }
        chunker = nil
    }

    /// One-off speech for the settings "Test voice" button.
    func speak(_ text: String) {
        let clean = VoiceTextProcessing.stripCitations(text)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { return }
        stop()
        enqueue(clean)
    }

    /// Stop immediately and discard any queued sentences.
    func stop() {
        chunker = nil
        if synthesizer.isSpeaking {
            synthesizer.stopSpeaking(at: .immediate)
        }
    }

    private func enqueue(_ text: String) {
        let utterance = AVSpeechUtterance(string: text)
        if let id = voiceIdentifier, let voice = AVSpeechSynthesisVoice(identifier: id) {
            utterance.voice = voice
        }
        utterance.rate = VoiceTextProcessing.avSpeechRate(
            fromConfigRate: configRate,
            min: AVSpeechUtteranceMinimumSpeechRate,
            defaultRate: AVSpeechUtteranceDefaultSpeechRate,
            max: AVSpeechUtteranceMaximumSpeechRate)
        utterance.volume = VoiceTextProcessing.avSpeechVolume(fromConfigVolume: configVolume)
        synthesizer.speak(utterance)
    }

    // MARK: - Offline synthesis to WAV (task #106, companion voice path)

    /// Renders `text` to an in-memory 16-bit PCM WAV via AVSpeechSynthesizer.write,
    /// so the Windows VR companion can play the Runner's spoken reply locally
    /// (the /api/voice/query returnAudio path). Uses a dedicated synthesizer per
    /// call — never the streaming `synthesizer` above — because write() and
    /// speak() cannot share an instance. The completion fires on the synthesizer's
    /// callback queue; the caller hops to wherever it needs.
    func synthesizeToWav(
        text: String,
        voiceId: String?,
        rate: Int,
        volume: Int,
        completion: @escaping (Result<Data, Error>) -> Void
    ) {
        let clean = VoiceTextProcessing.stripCitations(text)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else {
            completion(.failure(VoiceSynthesisError.emptyText))
            return
        }

        let utterance = AVSpeechUtterance(string: clean)
        if let voiceId, let voice = AVSpeechSynthesisVoice(identifier: voiceId) {
            utterance.voice = voice
        }
        utterance.rate = VoiceTextProcessing.avSpeechRate(
            fromConfigRate: rate,
            min: AVSpeechUtteranceMinimumSpeechRate,
            defaultRate: AVSpeechUtteranceDefaultSpeechRate,
            max: AVSpeechUtteranceMaximumSpeechRate)
        utterance.volume = VoiceTextProcessing.avSpeechVolume(fromConfigVolume: volume)

        // Retain the writer for the duration of the async write() so it isn't
        // deallocated mid-render; dropped from the set on completion.
        let writer = AVSpeechSynthesizer()
        writeLock.lock(); activeWriters.append(writer); writeLock.unlock()

        var pcm = Data()
        var sampleRate = 22050
        var channels = 1
        var finished = false

        let finish: (Result<Data, Error>) -> Void = { [weak self] result in
            guard let self else { return }
            if finished { return }
            finished = true
            self.writeLock.lock()
            self.activeWriters.removeAll { $0 === writer }
            self.writeLock.unlock()
            completion(result)
        }

        writer.write(utterance) { buffer in
            guard let pcmBuffer = buffer as? AVAudioPCMBuffer else {
                finish(.failure(VoiceSynthesisError.unsupportedBuffer))
                return
            }
            if pcmBuffer.frameLength == 0 {
                // Empty buffer marks end-of-stream.
                if pcm.isEmpty {
                    finish(.failure(VoiceSynthesisError.noAudioProduced))
                } else {
                    finish(.success(VoiceRpcProtocol.wrapPcm16ToWav(pcm, sampleRate: sampleRate, channels: channels)))
                }
                return
            }
            sampleRate = Int(pcmBuffer.format.sampleRate)
            channels = Int(pcmBuffer.format.channelCount)
            Self.appendInt16PCM(from: pcmBuffer, into: &pcm)
        }
    }

    /// Lock + set guarding the per-call write synthesizers so concurrent
    /// companion requests don't race on retention.
    private let writeLock = NSLock()
    private var activeWriters: [AVSpeechSynthesizer] = []

    /// Append a buffer's samples as little-endian Int16 PCM. AVSpeechSynthesizer
    /// may hand back either Int16 or Float32 buffers depending on the voice, so
    /// handle both (float is scaled/clamped to the 16-bit range).
    private static func appendInt16PCM(from buffer: AVAudioPCMBuffer, into pcm: inout Data) {
        let frames = Int(buffer.frameLength)
        guard frames > 0 else { return }
        let channelCount = Int(buffer.format.channelCount)

        if let int16 = buffer.int16ChannelData {
            for frame in 0..<frames {
                for ch in 0..<channelCount {
                    var sample = int16[ch][frame].littleEndian
                    withUnsafeBytes(of: &sample) { pcm.append(contentsOf: $0) }
                }
            }
        } else if let floats = buffer.floatChannelData {
            for frame in 0..<frames {
                for ch in 0..<channelCount {
                    let clamped = max(-1.0, min(1.0, floats[ch][frame]))
                    var sample = Int16(clamped * 32767.0).littleEndian
                    withUnsafeBytes(of: &sample) { pcm.append(contentsOf: $0) }
                }
            }
        }
    }
}

/// Failure modes for the offline TTS-to-WAV path (task #106).
enum VoiceSynthesisError: LocalizedError {
    case emptyText
    case unsupportedBuffer
    case noAudioProduced

    var errorDescription: String? {
        switch self {
        case .emptyText: return "Nothing to speak."
        case .unsupportedBuffer: return "The speech synthesizer returned an unsupported audio buffer."
        case .noAudioProduced: return "The speech synthesizer produced no audio."
        }
    }
}

// MARK: - Speech to text

enum SpeechToTextError: LocalizedError {
    case notAuthorized
    case recognizerUnavailable
    case onDeviceUnsupported
    case engineFailure(String)
    case noSpeechDetected
    case micSilent
    case recognitionFailed(String)

    var errorDescription: String? {
        switch self {
        case .notAuthorized:
            return "Microphone or speech-recognition access was denied. Enable it in System Settings → Privacy & Security."
        case .recognizerUnavailable:
            return "Speech recognition is unavailable on this device."
        case .onDeviceUnsupported:
            return "On-device speech recognition isn't available for English on this Mac. (Free-AI-SSD never sends audio off the machine.)"
        case .engineFailure(let m):
            return "Voice capture failed: \(m)"
        case .noSpeechDetected:
            return "Didn't catch any speech."
        case .micSilent:
            return "The microphone is on but no sound is reaching the app. macOS may be feeding it silence — quit and reopen Free AI SSD, and re-grant Microphone access in System Settings → Privacy & Security → Microphone."
        case .recognitionFailed(let m):
            return "Speech recognition failed: \(m)"
        }
    }
}

/// Wraps SFSpeechRecognizer + AVAudioEngine for offline (on-device) dictation.
/// The recognizer is pinned to on-device recognition so audio never leaves the
/// machine — consistent with the product's fully-offline posture. Mirrors the
/// Windows Whisper STT role: capture mic, transcribe, hand text back for the
/// prompt/auto-send path.
final class MacSpeechToText {
    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "en-US"))
    private let audioEngine = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var latestTranscript: String = ""
    private var finished = false

    // Diagnostics so an empty transcript can be attributed to the right cause:
    // a silent mic (macOS feeding the unsigned app zeros) vs. a recognizer that
    // errored vs. genuine silence from the user.
    private var peakLevel: Float = 0          // loudest sample seen this session (0…1)
    private var recognitionError: Error?       // last error from the recognition task
    // Peaks below this are indistinguishable from a dead/zeroed input stream.
    private let silenceFloor: Float = 0.001

    private(set) var isRecording = false

    /// Whether on-device English recognition is usable right now.
    var isAvailable: Bool {
        guard let recognizer else { return false }
        return recognizer.isAvailable && recognizer.supportsOnDeviceRecognition
    }

    /// Request both microphone and speech-recognition permission. The completion
    /// fires on the main queue with the combined grant. Both usage strings are
    /// declared in Runner.app's Info.plist (mic + speech), so these calls show
    /// the standard system prompts rather than crashing.
    static func requestAuthorization(_ completion: @escaping (Bool) -> Void) {
        SFSpeechRecognizer.requestAuthorization { speechStatus in
            let speechOK = (speechStatus == .authorized)
            AVCaptureDevice.requestAccess(for: .audio) { micOK in
                DispatchQueue.main.async { completion(speechOK && micOK) }
            }
        }
    }

    /// Start capturing mic audio and streaming it to the recognizer. `onPartial`
    /// fires on the main queue with the running transcript so the UI can show
    /// live text. Call `stop` to finalize.
    func start(onPartial: @escaping (String) -> Void) throws {
        guard !isRecording else { return }
        guard let recognizer, recognizer.isAvailable else {
            throw SpeechToTextError.recognizerUnavailable
        }
        guard recognizer.supportsOnDeviceRecognition else {
            throw SpeechToTextError.onDeviceUnsupported
        }

        latestTranscript = ""
        finished = false
        peakLevel = 0
        recognitionError = nil

        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        // Never send audio off-device — the whole product runs offline.
        request.requiresOnDeviceRecognition = true
        self.request = request

        let input = audioEngine.inputNode
        let format = input.outputFormat(forBus: 0)
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { [weak self] buffer, _ in
            guard let self else { return }
            self.request?.append(buffer)
            self.trackPeak(buffer)
        }

        audioEngine.prepare()
        do {
            try audioEngine.start()
        } catch {
            input.removeTap(onBus: 0)
            self.request = nil
            throw SpeechToTextError.engineFailure(error.localizedDescription)
        }

        isRecording = true
        task = recognizer.recognitionTask(with: request) { [weak self] result, error in
            guard let self else { return }
            if let error {
                // Don't lose the real reason — stop() uses it to explain an
                // empty transcript instead of the generic "no speech" message.
                self.recognitionError = error
            }
            if let result {
                self.latestTranscript = result.bestTranscription.formattedString
                let snapshot = self.latestTranscript
                DispatchQueue.main.async { onPartial(snapshot) }
            }
        }
    }

    /// Records the loudest sample in a captured buffer so we can later tell a
    /// silent input stream apart from a recognizer that simply found no words.
    private func trackPeak(_ buffer: AVAudioPCMBuffer) {
        guard let channels = buffer.floatChannelData else { return }
        let frames = Int(buffer.frameLength)
        guard frames > 0 else { return }
        var localPeak: Float = 0
        for ch in 0..<Int(buffer.format.channelCount) {
            let samples = channels[ch]
            for i in 0..<frames {
                let mag = abs(samples[i])
                if mag > localPeak { localPeak = mag }
            }
        }
        if localPeak > peakLevel { peakLevel = localPeak }
    }

    /// Stop capturing and return the final transcript on the main queue.
    func stop(completion: @escaping (Result<String, Error>) -> Void) {
        guard isRecording else {
            completion(.failure(SpeechToTextError.engineFailure("not recording")))
            return
        }
        isRecording = false

        audioEngine.stop()
        audioEngine.inputNode.removeTap(onBus: 0)
        request?.endAudio()

        // Give the recognizer a moment to emit its final result, then take the
        // best transcript we have. On-device finalization can lag the last audio
        // buffer by a few hundred ms, so wait long enough to avoid clipping a
        // result that is about to arrive.
        let deadline = DispatchTime.now() + 0.8
        DispatchQueue.main.asyncAfter(deadline: deadline) { [weak self] in
            guard let self else { return }
            self.task?.finish()
            self.task = nil
            self.request = nil
            let text = self.latestTranscript.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty {
                completion(.success(text))
                return
            }
            // Empty transcript — attribute it to the real cause so the user gets
            // an actionable message instead of a blanket "no speech."
            if self.peakLevel < self.silenceFloor {
                completion(.failure(SpeechToTextError.micSilent))
            } else if let err = self.recognitionError {
                completion(.failure(SpeechToTextError.recognitionFailed(err.localizedDescription)))
            } else {
                completion(.failure(SpeechToTextError.noSpeechDetected))
            }
        }
    }

    // MARK: - File recognition (task #106, companion voice path)

    private let fileTaskLock = NSLock()
    private var fileTasks: [SFSpeechRecognitionTask] = []

    /// Transcribe a WAV buffer on-device for the companion voice path. The
    /// companion's /api/voice/query upload (16-bit mono 16 kHz) arrives at the
    /// sidecar, which RPCs the WAV here; we run SFSpeechURLRecognitionRequest
    /// pinned to on-device (`requiresOnDeviceRecognition`, decision #168) so the
    /// audio never leaves the machine. Independent of the live-mic `start`/`stop`
    /// path above — safe to call while that's idle. Completion fires once.
    func recognizeFile(wavData: Data, completion: @escaping (Result<String, Error>) -> Void) {
        ensureAuthorized { [weak self] granted in
            guard let self else { return }
            guard granted else {
                completion(.failure(SpeechToTextError.notAuthorized))
                return
            }
            guard let recognizer = self.recognizer, recognizer.isAvailable else {
                completion(.failure(SpeechToTextError.recognizerUnavailable))
                return
            }
            guard recognizer.supportsOnDeviceRecognition else {
                completion(.failure(SpeechToTextError.onDeviceUnsupported))
                return
            }

            let tmpUrl = FileManager.default.temporaryDirectory
                .appendingPathComponent("free-ai-stt-\(UUID().uuidString).wav")
            do {
                try wavData.write(to: tmpUrl)
            } catch {
                completion(.failure(SpeechToTextError.engineFailure(error.localizedDescription)))
                return
            }

            let request = SFSpeechURLRecognitionRequest(url: tmpUrl)
            request.requiresOnDeviceRecognition = true
            request.shouldReportPartialResults = false

            var done = false
            var taskRef: SFSpeechRecognitionTask?
            let settle: (Result<String, Error>) -> Void = { [weak self] result in
                if done { return }
                done = true
                try? FileManager.default.removeItem(at: tmpUrl)
                if let self, let task = taskRef {
                    self.fileTaskLock.lock()
                    self.fileTasks.removeAll { $0 === task }
                    self.fileTaskLock.unlock()
                }
                completion(result)
            }

            let task = recognizer.recognitionTask(with: request) { result, error in
                if let result, result.isFinal {
                    let text = result.bestTranscription.formattedString
                        .trimmingCharacters(in: .whitespacesAndNewlines)
                    settle(.success(text))
                } else if let error {
                    settle(.failure(SpeechToTextError.recognitionFailed(error.localizedDescription)))
                }
                // Non-final results without error: keep waiting for the final one.
            }
            taskRef = task
            self.fileTaskLock.lock(); self.fileTasks.append(task); self.fileTaskLock.unlock()
        }
    }

    /// Resolve speech-recognition authorization for the server path, requesting
    /// it if the user hasn't decided yet. Unlike the interactive mic flow this
    /// only needs speech permission (no mic capture), so it doesn't prompt for
    /// the microphone.
    private func ensureAuthorized(_ completion: @escaping (Bool) -> Void) {
        switch SFSpeechRecognizer.authorizationStatus() {
        case .authorized:
            completion(true)
        case .notDetermined:
            SFSpeechRecognizer.requestAuthorization { status in
                DispatchQueue.main.async { completion(status == .authorized) }
            }
        default:
            completion(false)
        }
    }

    /// Tear down without producing a result (lock / app quit).
    func cancel() {
        isRecording = false
        if audioEngine.isRunning {
            audioEngine.stop()
            audioEngine.inputNode.removeTap(onBus: 0)
        }
        task?.cancel()
        task = nil
        request = nil
        latestTranscript = ""
        peakLevel = 0
        recognitionError = nil
    }
}
