//! In-process speech through Gradium's Phonon (github.com/gradium-ai/xn-ptts).
//!
//! Phonon is a 31M/40M-parameter flow-matching model that streams 80 ms frames as it generates
//! them: first audio in tens of milliseconds, about 10x realtime on a few CPU threads. The model
//! ships next to the executable (`models/phonon-31m-en`) or downloads once into
//! `paths::models_dir()`, pinned to the commit the runtime pins and checked by SHA-256.
//! Catalogue voices download on first use. A custom checkpoint folder (for the gated
//! multilingual 40M models) can be chosen in Settings.

use std::collections::{HashMap, HashSet};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::{Mutex, Once};
use std::time::{Duration, Instant};

use ptts::checkpoint::read_config;
use ptts::preprocess::Normalize;
use ptts::synth::{DeviceKind, Quant, SpeechOptions, Synth, SynthBuilder};
use sha2::{Digest, Sha256};

use crate::{log, paths, voices};

pub const SAMPLE_RATE: u32 = 24_000;
pub const DEFAULT_MODEL_ID: &str = "phonon-31m-en";
/// The commit the ptts 0.6.0 runtime itself pins for its default model.
pub const MODEL_REVISION: &str = "8c64b55ec301728e2d1695bfea87a08a267d0c2d";
/// The commit the runtime pins for its voice catalogue; assets/phonon-voices.jsonl is this snapshot.
pub const VOICE_REVISION: &str = "da6c9ec0d47be8df5c87c730a3aa97187f3229c3";
/// Largest |duration_delta| the model speaks reliably; about 0.67x-1.5x of its natural rate.
pub const MAX_DURATION_DELTA: f64 = 0.8;

pub struct ModelFile {
    pub name: &'static str,
    pub size: u64,
    pub sha256: &'static str,
}

/// The files of the pinned default checkpoint.
pub const MODEL_FILES: [ModelFile; 4] = [
    ModelFile {
        name: "config.json",
        size: 2_851,
        sha256: "7062ea376e6b158543c8abec8e5af67df171c49ad70eb7e2a0fb25c16f265e06",
    },
    ModelFile {
        name: "tokenizer.json",
        size: 277_967,
        sha256: "691a00068fa7b989f2f3e5ae5daa2d1a5bb319f2d5360deafcfb19a8bb6e7936",
    },
    ModelFile {
        name: "model.q8.gguf",
        size: 64_512_352,
        sha256: "a7d9aad2cd6dc66473f24a08bc6d2939030ef4d751ef82349361fbef3272dbae",
    },
    ModelFile {
        name: "default-voice.safetensors",
        size: 256_136,
        sha256: "f739e8c4c76ee7f92e7c94a82002748c8cd22e99582ac49f7b9b94cd63786e18",
    },
];

/// Weight files tried in order, with the format each loads as.
const WEIGHTS: [(&str, Quant); 2] = [("model.q8.gguf", Quant::Q80), ("model.safetensors", Quant::F32)];

#[derive(Debug)]
pub struct PhononError(pub String);

impl std::fmt::Display for PhononError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for PhononError {}

type Result<T> = std::result::Result<T, PhononError>;

fn fail<T>(message: impl Into<String>) -> Result<T> {
    Err(PhononError(message.into()))
}

/// Phonon's duration_delta condition for a speed multiplier (negative = faster).
///
/// Measured on phonon-31m-en: -0.6 → 1.32x, -0.3 → 1.19x, +0.5 → 0.82x, i.e. speed ≈ exp(-delta/2).
pub fn duration_delta_for_speed(speed: f64) -> f32 {
    if !speed.is_finite() || speed <= 0.0 {
        return 0.0;
    }
    (-2.0 * speed.ln()).clamp(-MAX_DURATION_DELTA, MAX_DURATION_DELTA) as f32
}

/// One autoregressive stream saturates at a few threads: on a 12-core Ryzen AI 9 HX 370, 3-4
/// threads gave 9-11x realtime and one per logical core only 5x.
pub fn default_threads() -> usize {
    let cpus = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4);
    (cpus / 4).clamp(2, 4)
}

pub fn model_cache_dir() -> PathBuf {
    paths::models_dir().join(DEFAULT_MODEL_ID)
}

pub fn voice_cache_dir() -> PathBuf {
    paths::models_dir().join("phonon-voices")
}

pub fn is_checkpoint(dir: &Path) -> bool {
    dir.join("config.json").is_file()
        && dir.join("tokenizer.json").is_file()
        && WEIGHTS.iter().any(|(w, _)| dir.join(w).is_file())
}

fn default_checkpoint_complete(dir: &Path) -> bool {
    MODEL_FILES.iter().all(|f| std::fs::metadata(dir.join(f.name)).is_ok_and(|m| m.len() == f.size))
}

/// The checkpoint folder to load: `custom` when set, otherwise a bundled copy, otherwise the
/// per-user cache. Ok(None) when the default model still has to be downloaded.
pub fn resolve_model_dir(custom: &str) -> Result<Option<PathBuf>> {
    let custom = custom.trim();
    if !custom.is_empty() {
        let dir = PathBuf::from(custom);
        if is_checkpoint(&dir) {
            return Ok(Some(dir));
        }
        return fail(format!(
            "The Phonon model folder '{custom}' has no config.json, tokenizer.json and weights (model.q8.gguf or model.safetensors)."
        ));
    }
    for root in paths::resource_dirs() {
        let candidate = root.join("models").join(DEFAULT_MODEL_ID);
        if default_checkpoint_complete(&candidate) {
            return Ok(Some(candidate));
        }
    }
    let cache = model_cache_dir();
    Ok(default_checkpoint_complete(&cache).then_some(cache))
}

fn agent() -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_connect(Some(Duration::from_secs(15)))
        .user_agent(concat!("DynamiteTTS/", env!("CARGO_PKG_VERSION")))
        .build()
        .into()
}

fn sha256_file(path: &Path) -> std::io::Result<String> {
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = vec![0u8; 1 << 16];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finalize().iter().map(|b| format!("{b:02x}")).collect())
}

/// Downloads `url` to `target` through a temporary file, checking size and hash when given.
fn download(
    url: &str,
    target: &Path,
    size: Option<u64>,
    sha256: Option<&str>,
    cancelled: &dyn Fn() -> bool,
    progress: &mut dyn FnMut(u64),
) -> Result<()> {
    let name = target.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let response = agent().get(url).call().map_err(|e| PhononError(format!("Could not download {name}: {e}")))?;
    let tmp = target.with_extension("part");
    let result = (|| {
        let mut reader = response.into_body().into_reader();
        let mut out = std::fs::File::create(&tmp).map_err(|e| PhononError(format!("Could not write {name}: {e}")))?;
        let mut buf = vec![0u8; 1 << 16];
        let mut received = 0u64;
        loop {
            if cancelled() {
                return fail("cancelled");
            }
            let n = reader.read(&mut buf).map_err(|e| PhononError(format!("Download of {name} failed: {e}")))?;
            if n == 0 {
                break;
            }
            out.write_all(&buf[..n]).map_err(|e| PhononError(format!("Could not write {name}: {e}")))?;
            received += n as u64;
            progress(received);
        }
        drop(out);
        if size.is_some_and(|s| s != received) {
            return fail(format!("{name} downloaded with the wrong size."));
        }
        if let Some(expected) = sha256 {
            let actual = sha256_file(&tmp).map_err(|e| PhononError(e.to_string()))?;
            if !actual.eq_ignore_ascii_case(expected) {
                return fail(format!("{name} failed its checksum."));
            }
        }
        std::fs::rename(&tmp, target).map_err(|e| PhononError(format!("Could not save {name}: {e}")))
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
    result
}

/// Downloads the default checkpoint into the cache, skipping files already there.
/// `progress` gets the fraction done.
pub fn ensure_default_model(cancelled: &dyn Fn() -> bool, progress: &mut dyn FnMut(f64)) -> Result<PathBuf> {
    let dir = model_cache_dir();
    std::fs::create_dir_all(&dir).map_err(|e| PhononError(format!("Could not create {}: {e}", dir.display())))?;
    let total: u64 = MODEL_FILES.iter().map(|f| f.size).sum();
    let mut done = 0u64;
    for file in &MODEL_FILES {
        let target = dir.join(file.name);
        let present = std::fs::metadata(&target).is_ok_and(|m| m.len() == file.size)
            && sha256_file(&target).is_ok_and(|h| h == file.sha256);
        if !present {
            let url =
                format!("https://huggingface.co/gradium/{DEFAULT_MODEL_ID}/resolve/{MODEL_REVISION}/{}", file.name);
            let base = done;
            download(&url, &target, Some(file.size), Some(file.sha256), cancelled, &mut |n| {
                progress((base + n) as f64 / total as f64)
            })?;
        }
        done += file.size;
        progress(done as f64 / total as f64);
    }
    Ok(dir)
}

/// The local file of a catalogue voice, downloading it once.
pub fn ensure_voice_file(voice: &voices::Voice, cancelled: &dyn Fn() -> bool) -> Result<PathBuf> {
    let dir = voice_cache_dir();
    std::fs::create_dir_all(&dir).map_err(|e| PhononError(e.to_string()))?;
    let target = dir.join(format!("{}.safetensors", voice.voice_id));
    if std::fs::metadata(&target).is_ok_and(|m| m.len() > 1_000) {
        return Ok(target);
    }
    log!("phonon: downloading voice {}", voice.name);
    let url = format!("https://huggingface.co/gradium/phonon-voices/resolve/{VOICE_REVISION}/{}", voice.filename);
    download(&url, &target, None, None, cancelled, &mut |_| {})?;
    Ok(target)
}

struct Loaded {
    synth: Synth,
    normalize: Normalize,
    dir: PathBuf,
    language: String,
    duration_delta: Option<f32>,
    voices: HashSet<String>,
}

/// What a speak call reports while it prepares, for the status pill.
pub enum Progress {
    Downloading(f64),
    Loading,
}

/// One loaded model, kept for the life of the process and reloaded only when the model folder
/// or language changes.
#[derive(Default)]
pub struct Engine {
    loaded: Mutex<Option<Loaded>>,
}

static THREADS: Once = Once::new();

fn open(dir: &Path, language: &str) -> Result<Loaded> {
    let normalize =
        language.parse::<Normalize>().map_err(|e| PhononError(format!("Phonon language '{language}': {e}")))?;
    let config = read_config(dir.join("config.json")).map_err(|e| PhononError(e.to_string()))?;
    let (weights, quant) = WEIGHTS
        .iter()
        .map(|&(name, q)| (dir.join(name), q))
        .find(|(p, _)| p.is_file())
        .ok_or_else(|| PhononError(format!("No weights in {}.", dir.display())))?;
    // Normalization is applied here rather than by the Synth so the language can change without a reload.
    let mut builder = SynthBuilder::new(config, weights, Normalize::OFF)
        .tokenizer_file(dir.join("tokenizer.json"))
        .device(DeviceKind::Cpu)
        .quant(quant);
    for (name, path) in ptts::loader::checkpoint_voices(dir) {
        builder = builder.add_voice(name, path);
    }
    let synth = builder.build().map_err(|e| PhononError(format!("Phonon could not load: {e}")))?;
    let voices = synth.voices().into_iter().collect();
    Ok(Loaded {
        synth,
        normalize,
        dir: dir.to_path_buf(),
        language: language.to_string(),
        duration_delta: None,
        voices,
    })
}

fn panic_message(p: Box<dyn std::any::Any + Send>) -> String {
    p.downcast_ref::<&str>()
        .map(|s| s.to_string())
        .or_else(|| p.downcast_ref::<String>().cloned())
        .unwrap_or_else(|| "unknown".into())
}

impl Engine {
    pub fn is_loaded(&self) -> bool {
        self.loaded.lock().map(|l| l.is_some()).unwrap_or(false)
    }

    /// Speaks `chunks` in order, handing each decoded block of 24 kHz mono samples to `sink`
    /// as soon as it exists. `sink` returns false to stop; so does `cancelled`.
    pub fn speak(
        &self,
        chunks: &[String],
        voice: &str,
        speed: f64,
        model_dir: &str,
        language: &str,
        cancelled: &dyn Fn() -> bool,
        on_progress: &mut dyn FnMut(Progress),
        sink: &mut dyn FnMut(&[f32]) -> bool,
    ) -> Result<()> {
        let language = match language.trim().to_ascii_lowercase() {
            l if l.is_empty() => "en".to_string(),
            l => l,
        };
        let dir = match resolve_model_dir(model_dir)? {
            Some(dir) => dir,
            None => {
                on_progress(Progress::Downloading(0.0));
                ensure_default_model(cancelled, &mut |f| on_progress(Progress::Downloading(f)))
                    .map_err(|e| PhononError(format!("Phonon model download failed: {e}")))?
            }
        };
        if cancelled() {
            return Ok(());
        }
        let mut guard = self.loaded.lock().unwrap_or_else(|e| e.into_inner());
        let same = guard.as_ref().is_some_and(|l| same_dir(&l.dir, &dir) && l.language == language);
        if !same {
            on_progress(Progress::Loading);
            THREADS.call_once(|| xn::utils::set_num_threads(default_threads()));
            let started = Instant::now();
            // Drop the old model before loading the next so two are never resident.
            *guard = None;
            let loaded = std::panic::catch_unwind(|| open(&dir, &language))
                .unwrap_or_else(|p| fail(format!("Phonon crashed while loading: {}", panic_message(p))))?;
            log!(
                "phonon: loaded {} (ptts {}, {} threads, lang {language}) in {} ms",
                dir.display(),
                env!("DT_PTTS_VERSION"),
                default_threads(),
                started.elapsed().as_millis()
            );
            *guard = Some(loaded);
        }
        let loaded = guard.as_mut().expect("loaded above");

        let delta = duration_delta_for_speed(speed);
        if loaded.duration_delta.is_none_or(|d| (d - delta).abs() > 1e-4) {
            let conditions = HashMap::from([("duration_delta".to_string(), delta.to_string())]);
            match loaded.synth.set_conditions(conditions) {
                Ok(()) => loaded.duration_delta = Some(delta),
                Err(e) => log!("phonon: duration_delta {delta:.2} rejected: {e}"),
            }
        }

        let voice = resolve_voice(loaded, voice, cancelled);
        let opts = SpeechOptions { voice, ..Default::default() };
        for chunk in chunks.iter().map(|c| c.trim()).filter(|c| !c.is_empty()) {
            if cancelled() {
                return Ok(());
            }
            let started = Instant::now();
            let text = loaded.normalize.apply(chunk);
            let synth = &loaded.synth;
            let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(
                || -> Result<(usize, Option<Duration>, bool)> {
                    let stream =
                        synth.stream_with(&text, &opts).map_err(|e| PhononError(format!("Phonon failed: {e}")))?;
                    let (mut samples, mut first) = (0usize, None);
                    // Dropping the stream on an early return stops generation and joins its workers.
                    for pcm in stream {
                        let pcm = pcm.map_err(|e| PhononError(format!("Phonon failed: {e}")))?;
                        if pcm.is_empty() {
                            continue;
                        }
                        first.get_or_insert_with(|| started.elapsed());
                        samples += pcm.len();
                        if cancelled() || !sink(&pcm) {
                            return Ok((samples, first, true));
                        }
                    }
                    Ok((samples, first, false))
                },
            ))
            .unwrap_or_else(|p| fail(format!("Phonon crashed: {}", panic_message(p))))?;
            let (samples, first, stopped) = outcome;
            log!(
                "phonon: {} chars -> {:.1}s audio, first audio {} ms, total {} ms",
                chunk.chars().count(),
                samples as f64 / SAMPLE_RATE as f64,
                first.unwrap_or_default().as_millis(),
                started.elapsed().as_millis()
            );
            if stopped {
                return Ok(());
            }
        }
        Ok(())
    }
}

fn same_dir(a: &Path, b: &Path) -> bool {
    match (a.canonicalize(), b.canonicalize()) {
        (Ok(a), Ok(b)) => a == b,
        _ => a == b,
    }
}

/// The voice name for the engine: None for the checkpoint's own voice, a voice the checkpoint
/// ships, or a catalogue voice (downloaded and registered on first use).
fn resolve_voice(loaded: &mut Loaded, requested: &str, cancelled: &dyn Fn() -> bool) -> Option<String> {
    let requested = requested.trim();
    if requested.is_empty() || requested.eq_ignore_ascii_case(voices::DEFAULT_ID) {
        return None;
    }
    if let Some(v) = loaded.voices.iter().find(|v| v.eq_ignore_ascii_case(requested)) {
        return Some(v.clone());
    }
    let Some(voice) = voices::find(requested).filter(|v| !v.is_default()) else {
        log!("phonon: unknown voice '{requested}', using the model's default voice");
        return None;
    };
    if loaded.voices.contains(&voice.name) {
        return Some(voice.name.clone());
    }
    let file = match ensure_voice_file(voice, cancelled) {
        Ok(f) => f,
        Err(e) => {
            log!("phonon: could not download voice {}: {e}; using the default voice", voice.name);
            return None;
        }
    };
    match loaded.synth.add_voice_file(&voice.name, &file) {
        Ok(()) => {
            loaded.voices.insert(voice.name.clone());
            Some(voice.name.clone())
        }
        Err(e) => {
            log!("phonon: registering voice {} failed: {e}", voice.name);
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn speed_maps_to_a_clamped_duration_delta() {
        assert_eq!(duration_delta_for_speed(1.0), 0.0);
        assert!(duration_delta_for_speed(1.2) < 0.0, "faster is negative");
        assert!(duration_delta_for_speed(0.8) > 0.0);
        assert_eq!(duration_delta_for_speed(4.0), -0.8);
        assert_eq!(duration_delta_for_speed(0.25), 0.8);
        assert_eq!(duration_delta_for_speed(0.0), 0.0);
        assert_eq!(duration_delta_for_speed(f64::NAN), 0.0);
    }

    #[test]
    fn threads_stay_in_the_measured_sweet_spot() {
        assert!((2..=4).contains(&default_threads()));
    }

    #[test]
    fn a_custom_folder_must_be_a_checkpoint() {
        let dir = std::env::temp_dir().join(format!("dt-phonon-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        assert!(resolve_model_dir(dir.to_str().unwrap()).is_err());
        for f in ["config.json", "tokenizer.json", "model.safetensors"] {
            std::fs::write(dir.join(f), b"{}").unwrap();
        }
        assert_eq!(resolve_model_dir(dir.to_str().unwrap()).unwrap(), Some(dir.clone()));
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn hashes_files() {
        let path = std::env::temp_dir().join(format!("dt-hash-{}", std::process::id()));
        std::fs::write(&path, b"abc").unwrap();
        assert_eq!(sha256_file(&path).unwrap(), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        let _ = std::fs::remove_file(path);
    }
}
