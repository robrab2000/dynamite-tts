// C interface of dynamite_phonon.dll, the Phonon text-to-speech engine as Dynamite TTS drives it.
// Mirrors src/lib.rs; the C# side is windows/src/DynamiteTts/Native/PhononNative.cs.
//
// Booleans are int32_t (0 = false), never C bool, so every binding reads them the same way.
#ifndef DYNAMITE_PHONON_H
#define DYNAMITE_PHONON_H

#include <stddef.h>
#include <stdint.h>

typedef struct DpHandle DpHandle;

typedef struct {
    size_t samples;    // 24 kHz mono samples delivered to the callback
    double ttfa_ms;    // from the dp_speak call to the first audio
    double total_ms;   // the whole call
} DpResult;

// Audio as it is decoded, on the thread that called dp_speak. Return 0 to stop; dp_speak then
// returns 1 with what was produced so far.
typedef int32_t (*DpFrameFn)(const float *pcm, size_t n, void *user);

// Load the checkpoint folder `dir` (config.json, tokenizer.json, model.q8.gguf or
// model.safetensors, default-voice.safetensors, voices/*.safetensors). `lang` is the text
// normalization language (en, fr, de, es, pt, ... or none). `threads` is the CPU thread count
// for the whole process, 0 for xn's default; it is applied before the first load only.
// NULL on failure; dp_last_error(NULL) on the same thread says why.
DpHandle *dp_new(const char *dir, const char *lang, uint32_t threads);
// Speak `text` in `voice` (NULL = the checkpoint's default voice) at `temperature` (< 0 = the
// model's default). Blocks until the utterance is spoken or the callback stops it.
int32_t dp_speak(DpHandle *h, const char *text, const char *voice, float temperature,
                 DpFrameFn cb, void *user, DpResult *out);
// Register a voice file (safetensors) under `name`, usable from the next dp_speak.
int32_t dp_add_voice(DpHandle *h, const char *name, const char *path);
// Voice names, NUL-separated and ending in a second NUL. Borrowed; valid until the next
// dp_add_voice or dp_free.
const char *dp_voices(const DpHandle *h);
// Change the normalization language without reloading.
int32_t dp_set_language(DpHandle *h, const char *lang);
// Speaking rate through the model's duration_delta condition: 0 is the trained rate, negative is
// faster, positive slower. Small magnitudes work best. Applies from the next dp_speak.
int32_t dp_set_duration_delta(DpHandle *h, float duration_delta);
uint32_t dp_sample_rate(const DpHandle *h);
// The last error on `h`, or the last dp_new failure on this thread when `h` is NULL. Borrowed.
const char *dp_last_error(const DpHandle *h);
void dp_free(DpHandle *h);
// The ptts runtime version this library was built with, e.g. "0.6.0". Static.
const char *dp_runtime_version(void);

#endif
