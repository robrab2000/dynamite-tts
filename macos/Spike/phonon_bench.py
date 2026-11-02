#!/usr/bin/env python3
"""Measure the dynamite_phonon shim on this Mac: load time, time to first audio, realtime factor,
a mid-utterance cancel and a catalogue voice. Writes a WAV of the default voice.

    python3 -I macos/Spike/phonon_bench.py path/to/libdynamite_phonon.dylib [threads] [out.wav]
"""
import ctypes, os, struct, sys, time, wave

lib_path = sys.argv[1]
threads = int(sys.argv[2]) if len(sys.argv) > 2 else 4
out_wav = sys.argv[3] if len(sys.argv) > 3 else "/tmp/phonon-default-voice.wav"
support = os.path.expanduser("~/Library/Application Support/DynamiteTTS/models")
model = os.path.join(support, "phonon-31m-en")
easton = os.path.join(support, "phonon-voices", "LMDIYE0Oa377Qh6Z.safetensors")

SHORT = "The quick brown fox jumps over the lazy dog near the river bank."
PARA = ("Phonon is a small text to speech model that streams audio as it generates it. "
        "On an Apple Silicon laptop the whole paragraph renders many times faster than realtime, "
        "so playback can begin after the first few frames while the rest is still being produced. "
        "This paragraph exists to measure how long a longer passage takes from the first word to the last, "
        "and whether the speaking rate stays steady throughout.")

class DpResult(ctypes.Structure):
    _fields_ = [("samples", ctypes.c_size_t), ("ttfa_ms", ctypes.c_double), ("total_ms", ctypes.c_double)]

FRAME = ctypes.CFUNCTYPE(ctypes.c_int32, ctypes.POINTER(ctypes.c_float), ctypes.c_size_t, ctypes.c_void_p)
lib = ctypes.CDLL(lib_path)
lib.dp_new.restype = ctypes.c_void_p
lib.dp_new.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint32]
lib.dp_speak.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_float, FRAME, ctypes.c_void_p, ctypes.POINTER(DpResult)]
lib.dp_add_voice.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p]
lib.dp_set_duration_delta.argtypes = [ctypes.c_void_p, ctypes.c_float]
lib.dp_last_error.restype = ctypes.c_char_p
lib.dp_last_error.argtypes = [ctypes.c_void_p]
lib.dp_runtime_version.restype = ctypes.c_char_p
lib.dp_voices.restype = ctypes.POINTER(ctypes.c_char)
lib.dp_voices.argtypes = [ctypes.c_void_p]

t = time.perf_counter()
h = lib.dp_new(model.encode(), b"en", threads)
load_s = time.perf_counter() - t
if not h:
    sys.exit("load failed: %s" % lib.dp_last_error(None))
print(f"runtime ptts {lib.dp_runtime_version().decode()}  threads={threads}  load={load_s*1000:.0f} ms")

def speak(text, voice=None, stop_after=None, collect=False):
    chunks = []
    frames = [0]
    def cb(pcm, n, user):
        frames[0] += 1
        if collect:
            chunks.append(bytes(ctypes.string_at(pcm, n * 4)))
        if stop_after is not None and frames[0] >= stop_after:
            return 0
        return 1
    res = DpResult()
    t = time.perf_counter()
    ok = lib.dp_speak(h, text.encode(), voice.encode() if voice else None, -1.0, FRAME(cb), None, ctypes.byref(res))
    wall = time.perf_counter() - t
    if not ok:
        sys.exit("speak failed: %s" % lib.dp_last_error(h))
    audio_s = res.samples / 24000
    rtf = audio_s / (res.total_ms / 1000) if res.total_ms else 0
    print(f"  {len(text):4d} chars -> {audio_s:5.2f} s audio in {frames[0]:3d} frames; first audio {res.ttfa_ms:5.1f} ms; total {res.total_ms:7.1f} ms ({rtf:4.1f}x realtime); wall {wall*1000:.0f} ms")
    return b"".join(chunks), res

print("warm-up:")
speak("Ready.")
print("short sentence x3:")
for _ in range(3):
    audio, _ = speak(SHORT, collect=True)
print("paragraph x2:")
for _ in range(2):
    speak(PARA)
print("cancel after 6 frames (paragraph):")
t = time.perf_counter(); speak(PARA, stop_after=6); print(f"  returned {(time.perf_counter()-t)*1000:.0f} ms after the call")
print("speed 1.5x (delta -0.8) short sentence:")
lib.dp_set_duration_delta(h, -0.8); speak(SHORT); lib.dp_set_duration_delta(h, 0.0)
if os.path.exists(easton):
    if lib.dp_add_voice(h, b"Easton", easton.encode()):
        print("Easton (catalogue voice):")
        speak(SHORT, voice="Easton")
    else:
        print("add_voice failed:", lib.dp_last_error(h))

floats = struct.unpack(f"<{len(audio)//4}f", audio)
with wave.open(out_wav, "wb") as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(24000)
    w.writeframes(struct.pack(f"<{len(floats)}h", *(max(-32768, min(32767, int(s * 32767))) for s in floats)))
print("wrote", out_wav)
