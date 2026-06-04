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

    /// Installed voices. English voices first so the DCS/aviation use case lands
    /// on a sensible default, then the rest, each alphabetised by label.
    static func availableVoices() -> [TtsVoiceOption] {
        let voices = AVSpeechSynthesisVoice.speechVoices()
        let mapped = voices.map { v -> (option: TtsVoiceOption, en: Bool) in
            let lang = v.language
            return (TtsVoiceOption(id: v.identifier, label: "\(v.name) (\(lang))"), lang.hasPrefix("en"))
        }
        return mapped
            .sorted { a, b in
                if a.en != b.en { return a.en && !b.en }
                return a.option.label.localizedCaseInsensitiveCompare(b.option.label) == .orderedAscending
            }
            .map { $0.option }
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
}

// MARK: - Speech to text

enum SpeechToTextError: LocalizedError {
    case notAuthorized
    case recognizerUnavailable
    case onDeviceUnsupported
    case engineFailure(String)
    case noSpeechDetected

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

        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        // Never send audio off-device — the whole product runs offline.
        request.requiresOnDeviceRecognition = true
        self.request = request

        let input = audioEngine.inputNode
        let format = input.outputFormat(forBus: 0)
        input.installTap(onBus: 0, bufferSize: 1024, format: format) { [weak self] buffer, _ in
            self?.request?.append(buffer)
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
        task = recognizer.recognitionTask(with: request) { [weak self] result, _ in
            guard let self else { return }
            if let result {
                self.latestTranscript = result.bestTranscription.formattedString
                let snapshot = self.latestTranscript
                DispatchQueue.main.async { onPartial(snapshot) }
            }
        }
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

        // Give the recognizer a brief moment to emit its final result, then take
        // the best transcript we have. (On-device finalization is fast; we don't
        // block the UI waiting for an isFinal callback that may lag.)
        let deadline = DispatchTime.now() + 0.4
        DispatchQueue.main.asyncAfter(deadline: deadline) { [weak self] in
            guard let self else { return }
            self.task?.finish()
            self.task = nil
            self.request = nil
            let text = self.latestTranscript.trimmingCharacters(in: .whitespacesAndNewlines)
            if text.isEmpty {
                completion(.failure(SpeechToTextError.noSpeechDetected))
            } else {
                completion(.success(text))
            }
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
    }
}
