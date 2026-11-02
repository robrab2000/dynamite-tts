//! C interface to Phonon for Dynamite TTS.
//!
//! The upstream `ptts-ffi` crate exposes the same engine but fixes the speaking rate at the
//! model's default and selects the voice per handle. Dynamite needs a speed slider and switches
//! voices per utterance, so this crate drives [`ptts::synth::Synth`] directly: the
//! `duration_delta` condition is changed with `set_conditions` (no reload), the voice is a
//! per-call option, and audio streams to a callback that can stop generation early.
//!
//! `include/dynamite_phonon.h` declares this interface by hand; keep both in step.

use std::cell::RefCell;
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::Path;
use std::sync::Once;
use std::time::Instant;

use ptts::checkpoint::read_config;
use ptts::preprocess::Normalize;
use ptts::synth::{DeviceKind, Quant, SpeechOptions, Synth, SynthBuilder};

/// Weight files tried in order, with the format each loads as (same order as ptts-ffi).
const WEIGHTS: [(&str, Quant); 2] =
    [("model.q8.gguf", Quant::Q80), ("model.safetensors", Quant::F32)];

pub struct DpHandle {
    synth: Synth,
    normalize: Normalize,
    voice_blob: Vec<u8>,
    last_error: Option<CString>,
}

#[repr(C)]
pub struct DpResult {
    pub samples: usize,
    pub ttfa_ms: f64,
    pub total_ms: f64,
}

pub type DpFrameFn = extern "C" fn(*const f32, usize, *mut c_void) -> i32;

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

static THREADS: Once = Once::new();

fn c_string(e: &str) -> CString {
    CString::new(e.replace('\0', " ")).expect("NULs were replaced")
}

/// Run `f`, turning a panic into an error: unwinding across the C boundary aborts the process.
fn guarded<T>(f: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or_else(|p| {
        let msg = p
            .downcast_ref::<&str>()
            .map(|s| s.to_string())
            .or_else(|| p.downcast_ref::<String>().cloned())
            .unwrap_or_else(|| "unknown".into());
        Err(format!("internal error: {msg}"))
    })
}

fn nul_separated(names: &[String]) -> Vec<u8> {
    let mut blob = Vec::new();
    for n in names {
        blob.extend_from_slice(n.as_bytes());
        blob.push(0);
    }
    blob.push(0);
    blob
}

/// # Safety
/// `p` must be NULL or a NUL-terminated string.
unsafe fn opt_str(p: *const c_char) -> Option<String> {
    if p.is_null() {
        None
    } else {
        Some(unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned())
    }
}

fn open(dir: &Path, lang: &str) -> Result<DpHandle, String> {
    let normalize = lang.parse::<Normalize>().map_err(|e| e.to_string())?;
    let cfg = read_config(dir.join("config.json")).map_err(|e| e.to_string())?;
    let (weights, quant) = WEIGHTS
        .iter()
        .map(|&(name, q)| (dir.join(name), q))
        .find(|(p, _)| p.is_file())
        .ok_or_else(|| {
            format!(
                "no weights in {}; expected one of {}",
                dir.display(),
                WEIGHTS.map(|(name, _)| name).join(", ")
            )
        })?;
    // Normalization is applied here, not by the Synth, so the language can change without a
    // reload. The Synth applies its own (OFF) first thing anyway.
    let mut builder = SynthBuilder::new(cfg, weights, Normalize::OFF)
        .tokenizer_file(dir.join("tokenizer.json"))
        .device(DeviceKind::Cpu)
        .quant(quant);
    for (name, path) in ptts::loader::checkpoint_voices(dir) {
        builder = builder.add_voice(name, path);
    }
    let synth = builder.build().map_err(|e| e.to_string())?;
    let voice_blob = nul_separated(&synth.voices());
    Ok(DpHandle { synth, normalize, voice_blob, last_error: None })
}

/// # Safety
/// `dir` and `lang` must be NUL-terminated UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_new(
    dir: *const c_char,
    lang: *const c_char,
    threads: u32,
) -> *mut DpHandle {
    let dir = unsafe { opt_str(dir) }.unwrap_or_default();
    let lang = unsafe { opt_str(lang) }.unwrap_or_else(|| "none".into());
    if threads > 0 {
        THREADS.call_once(|| xn::utils::set_num_threads(threads as usize));
    }
    match guarded(|| open(Path::new(&dir), &lang)) {
        Ok(h) => Box::into_raw(Box::new(h)),
        Err(e) => {
            LAST_ERROR.with(|l| *l.borrow_mut() = Some(c_string(&e)));
            std::ptr::null_mut()
        }
    }
}

fn speak(
    h: &mut DpHandle,
    text: &str,
    voice: Option<String>,
    temperature: f32,
    sink: &mut dyn FnMut(&[f32]) -> bool,
) -> Result<DpResult, String> {
    let start = Instant::now();
    let opts = SpeechOptions {
        voice,
        temperature: (temperature >= 0.0).then_some(temperature),
        ..Default::default()
    };
    let text = h.normalize.apply(text);
    let stream = h.synth.stream_with(&text, &opts).map_err(|e| e.to_string())?;
    let (mut samples, mut ttfa) = (0usize, None);
    // Dropping the stream on an early return stops generation and joins its workers.
    for pcm in stream {
        let pcm = pcm.map_err(|e| e.to_string())?;
        if pcm.is_empty() {
            continue;
        }
        ttfa.get_or_insert_with(|| start.elapsed());
        samples += pcm.len();
        if !sink(&pcm) {
            break;
        }
    }
    Ok(DpResult {
        samples,
        ttfa_ms: ttfa.unwrap_or_default().as_secs_f64() * 1e3,
        total_ms: start.elapsed().as_secs_f64() * 1e3,
    })
}

/// # Safety
/// `h` must come from `dp_new`; `text` must be NUL-terminated UTF-8; `voice` NULL or the same;
/// `cb` valid for the call; `out` NULL or writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_speak(
    h: *mut DpHandle,
    text: *const c_char,
    voice: *const c_char,
    temperature: f32,
    cb: DpFrameFn,
    user: *mut c_void,
    out: *mut DpResult,
) -> i32 {
    let h = unsafe { &mut *h };
    let text = unsafe { opt_str(text) }.unwrap_or_default();
    let voice = unsafe { opt_str(voice) }.filter(|v| !v.is_empty());
    let r = guarded(|| {
        speak(h, &text, voice, temperature, &mut |pcm: &[f32]| {
            cb(pcm.as_ptr(), pcm.len(), user) != 0
        })
    });
    match r {
        Ok(r) => {
            if !out.is_null() {
                unsafe { *out = r };
            }
            1
        }
        Err(e) => {
            h.last_error = Some(c_string(&e));
            0
        }
    }
}

/// # Safety
/// `h` from `dp_new`; `name` and `path` NUL-terminated UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_add_voice(
    h: *mut DpHandle,
    name: *const c_char,
    path: *const c_char,
) -> i32 {
    let h = unsafe { &mut *h };
    let name = unsafe { opt_str(name) }.unwrap_or_default();
    let path = unsafe { opt_str(path) }.unwrap_or_default();
    match guarded(|| h.synth.add_voice_file(&name, Path::new(&path)).map_err(|e| e.to_string())) {
        Ok(()) => {
            h.voice_blob = nul_separated(&h.synth.voices());
            1
        }
        Err(e) => {
            h.last_error = Some(c_string(&e));
            0
        }
    }
}

/// # Safety
/// `h` from `dp_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_voices(h: *const DpHandle) -> *const c_char {
    unsafe { &*h }.voice_blob.as_ptr() as *const c_char
}

/// # Safety
/// `h` from `dp_new`; `lang` NUL-terminated UTF-8.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_set_language(h: *mut DpHandle, lang: *const c_char) -> i32 {
    let h = unsafe { &mut *h };
    let lang = unsafe { opt_str(lang) }.unwrap_or_default();
    match lang.parse::<Normalize>() {
        Ok(n) => {
            h.normalize = n;
            1
        }
        Err(e) => {
            h.last_error = Some(c_string(&e.to_string()));
            0
        }
    }
}

/// # Safety
/// `h` from `dp_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_set_duration_delta(h: *mut DpHandle, duration_delta: f32) -> i32 {
    let h = unsafe { &mut *h };
    if !duration_delta.is_finite() {
        h.last_error = Some(c_string("duration_delta must be finite"));
        return 0;
    }
    let conditions =
        HashMap::from([("duration_delta".to_string(), duration_delta.to_string())]);
    match guarded(|| h.synth.set_conditions(conditions).map_err(|e| e.to_string())) {
        Ok(()) => 1,
        Err(e) => {
            h.last_error = Some(c_string(&e));
            0
        }
    }
}

/// # Safety
/// `h` from `dp_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_sample_rate(h: *const DpHandle) -> u32 {
    unsafe { &*h }.synth.sample_rate()
}

/// # Safety
/// `h` from `dp_new`, or NULL for the last `dp_new` failure on this thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_last_error(h: *const DpHandle) -> *const c_char {
    if h.is_null() {
        return LAST_ERROR
            .with(|l| l.borrow().as_ref().map_or(std::ptr::null(), |s| s.as_ptr()));
    }
    unsafe { &*h }.last_error.as_ref().map_or(std::ptr::null(), |s| s.as_ptr())
}

/// # Safety
/// `h` from `dp_new`, freed once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dp_free(h: *mut DpHandle) {
    if !h.is_null() {
        drop(unsafe { Box::from_raw(h) });
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn dp_runtime_version() -> *const c_char {
    static VERSION: &[u8] = concat!(env!("DP_PTTS_VERSION"), "\0").as_bytes();
    VERSION.as_ptr() as *const c_char
}
