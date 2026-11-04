//! The built-in chat model for Summary, Explain and ELI5: llama.cpp in-process.
//!
//! The models are small Qwen3 instruct checkpoints (Apache 2.0), quantised to 4 bits. Each
//! downloads once into `paths::models_dir()/chat`, pinned to a commit and checked by SHA-256.
//! The model stays loaded between requests and is dropped after `IDLE_UNLOAD` without use;
//! each request gets a fresh context sized to its prompt, so memory for the attention cache
//! is only held while a reply is being written.

// Without llama.cpp the prompt helpers below have no caller outside the tests.
#![cfg_attr(not(feature = "local-llm"), allow(dead_code))]

use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use crate::download::{self, ModelFile};
use crate::paths;

/// How long the model may sit unused before its memory is given back.
pub const IDLE_UNLOAD: Duration = Duration::from_secs(5 * 60);
/// The largest context a request may use: prompt plus reply, in tokens.
pub const MAX_CONTEXT: u32 = 4096;
/// Appended after the assistant header so Qwen3 answers straight away instead of thinking first.
pub const NO_THINK_PREFILL: &str = "<think>\n\n</think>\n\n";
/// llama.cpp's working buffers for one request. A ~3,500-token Summary on an M1 Max peaked at
/// 1.66 GB (1.7B) and 0.94 GB (0.6B) resident, about 80 MB over weights plus attention cache;
/// this leaves room for the CPU backends' larger buffers.
const COMPUTE_BYTES: u64 = 128 << 20;

pub struct ChatModel {
    /// The value stored in Settings.
    pub id: &'static str,
    pub title: &'static str,
    /// One line for Settings.
    pub note: &'static str,
    pub repo: &'static str,
    pub revision: &'static str,
    pub file: ModelFile,
    /// Attention-cache bytes per token of context: 2 (K and V) × layers × KV heads × head size × 2 bytes.
    pub kv_bytes_per_token: u64,
}

pub const DEFAULT_MODEL_ID: &str = "qwen3-1.7b";

pub const CHAT_MODELS: [ChatModel; 2] = [
    ChatModel {
        id: "qwen3-1.7b",
        title: "Qwen3 1.7B",
        note: "Recommended.",
        repo: "unsloth/Qwen3-1.7B-GGUF",
        revision: "d7f544eead698dbd1f15126ef60b45a1e1933222",
        file: ModelFile {
            name: "Qwen3-1.7B-Q4_K_M.gguf",
            size: 1_107_409_472,
            sha256: "b139949c5bd74937ad8ed8c8cf3d9ffb1e99c866c823204dc42c0d91fa181897",
        },
        kv_bytes_per_token: 2 * 28 * 8 * 128 * 2,
    },
    ChatModel {
        id: "qwen3-0.6b",
        title: "Qwen3 0.6B",
        note: "Lighter, for machines with 8 GB of memory or less.",
        repo: "unsloth/Qwen3-0.6B-GGUF",
        revision: "50968a4468ef4233ed78cd7c3de230dd1d61a56b",
        file: ModelFile {
            name: "Qwen3-0.6B-Q4_K_M.gguf",
            size: 396_705_472,
            sha256: "ac2d97712095a558e31573f62f466a3f9d93990898b0ec79d7c974c1780d524a",
        },
        kv_bytes_per_token: 2 * 28 * 8 * 128 * 2,
    },
];

#[derive(Debug)]
pub struct LlmError(pub String);

impl std::fmt::Display for LlmError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for LlmError {}

type Result<T> = std::result::Result<T, LlmError>;

/// The model with this id, or the default one.
pub fn find_model(id: &str) -> &'static ChatModel {
    let id = id.trim();
    CHAT_MODELS.iter().find(|m| m.id.eq_ignore_ascii_case(id)).unwrap_or(&CHAT_MODELS[0])
}

pub fn model_dir() -> PathBuf {
    paths::models_dir().join("chat")
}

pub fn model_path(model: &ChatModel) -> PathBuf {
    model_dir().join(model.file.name)
}

/// A cheap check for the Settings window: the file is there with the right size.
pub fn is_downloaded(model: &ChatModel) -> bool {
    std::fs::metadata(model_path(model)).is_ok_and(|m| m.len() == model.file.size)
}

impl ChatModel {
    /// Estimated peak memory while a reply is being written: the weights, the attention cache
    /// for a full context, and llama.cpp's working buffers.
    pub fn memory_estimate(&self) -> u64 {
        self.file.size + self.kv_bytes_per_token * u64::from(MAX_CONTEXT) + COMPUTE_BYTES
    }
}

/// "1.1 GB", "397 MB".
pub fn size_label(bytes: u64) -> String {
    let mb = bytes as f64 / 1_000_000.0;
    if mb >= 1000.0 { format!("{:.1} GB", mb / 1000.0) } else { format!("{mb:.0} MB") }
}

/// Downloads the model once; `progress` gets the fraction done.
pub fn ensure_model(model: &ChatModel, cancelled: &dyn Fn() -> bool, progress: &mut dyn FnMut(f64)) -> Result<PathBuf> {
    let dir = model_dir();
    std::fs::create_dir_all(&dir).map_err(|e| LlmError(format!("Could not create {}: {e}", dir.display())))?;
    let target = model_path(model);
    if download::is_intact(&target, &model.file) {
        return Ok(target);
    }
    let url = format!("https://huggingface.co/{}/resolve/{}/{}", model.repo, model.revision, model.file.name);
    let size = model.file.size;
    download::download(&url, &target, Some(size), Some(model.file.sha256), cancelled, &mut |n| {
        progress(n as f64 / size as f64)
    })
    .map_err(LlmError)?;
    Ok(target)
}

/// What a chat call reports as it goes, for the status pill.
pub enum Progress {
    Downloading(f64),
    Loading,
    /// The model is ready and writing the reply.
    Writing,
}

/// The answer after any `<think>…</think>` block, trimmed.
pub fn strip_thinking(reply: &str) -> String {
    match reply.rfind("</think>") {
        Some(i) => &reply[i + "</think>".len()..],
        None => reply,
    }
    .trim()
    .to_string()
}

/// The first `max_chars` characters of `text`, with "…" when something was cut.
pub fn truncate_chars(text: &str, max_chars: usize) -> String {
    match text.char_indices().nth(max_chars) {
        Some((i, _)) => format!("{}…", text[..i].trim_end()),
        None => text.to_string(),
    }
}

/// A chat model scales further than Phonon's single stream; leave a couple of cores for the rest.
pub fn default_threads() -> i32 {
    let cpus = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4);
    (cpus.saturating_sub(2) as i32).clamp(2, 8)
}

/// The context for a prompt of `prompt_tokens` with room for `max_tokens` more, rounded up to 256.
pub fn context_size(prompt_tokens: usize, max_tokens: u32) -> u32 {
    let needed = prompt_tokens as u32 + max_tokens + 16;
    needed.div_ceil(256).saturating_mul(256).min(MAX_CONTEXT)
}

/// One chat request: the system prompt, optional worked examples (user, assistant turns sent
/// before `user`), the user message, and how the reply is sampled.
pub struct Request<'a> {
    pub system: &'a str,
    pub shots: &'a [(&'a str, &'a str)],
    pub user: &'a str,
    pub max_tokens: u32,
    /// None for Qwen3's recommended 0.7.
    pub temperature: Option<f32>,
}

impl<'a> Request<'a> {
    pub fn new(system: &'a str, user: &'a str, max_tokens: u32) -> Self {
        Request { system, shots: &[], user, max_tokens, temperature: None }
    }
}

/// Short title for a failed request in `mode` (Summary, Explain or ELI5).
pub fn failure_title(message: &str, mode: &str) -> String {
    let m = message.to_lowercase();
    if ["could not download", "download of", "checksum", "wrong size"].iter().any(|p| m.contains(p)) {
        return "Chat model download failed".into();
    }
    if ["memory", "alloc"].iter().any(|p| m.contains(p)) {
        return "Not enough memory for the chat model".into();
    }
    format!("{mode} failed")
}

#[cfg(feature = "local-llm")]
mod engine {
    use std::num::NonZeroU32;
    use std::sync::OnceLock;

    use llama_cpp_2::context::params::LlamaContextParams;
    use llama_cpp_2::llama_backend::LlamaBackend;
    use llama_cpp_2::llama_batch::LlamaBatch;
    use llama_cpp_2::model::params::LlamaModelParams;
    use llama_cpp_2::model::{LlamaChatMessage, LlamaChatTemplate, LlamaModel};
    use llama_cpp_2::sampling::LlamaSampler;
    use llama_cpp_2::token::LlamaToken;

    use super::*;
    use crate::log;

    pub struct Loaded {
        pub id: &'static str,
        pub model: LlamaModel,
    }

    fn fail(what: &str, e: impl std::fmt::Display) -> LlmError {
        LlmError(format!("{what}: {e}"))
    }

    /// llama.cpp may be initialised once per process.
    pub fn backend() -> Result<&'static LlamaBackend> {
        static BACKEND: OnceLock<std::result::Result<LlamaBackend, String>> = OnceLock::new();
        BACKEND
            .get_or_init(|| {
                // Before init, so the GPU set-up messages are silenced too.
                llama_cpp_2::send_logs_to_tracing(llama_cpp_2::LogOptions::default().with_logs_enabled(false));
                LlamaBackend::init().map_err(|e| e.to_string())
            })
            .as_ref()
            .map_err(|e| LlmError(format!("Could not start llama.cpp: {e}")))
    }

    pub fn load(model: &'static ChatModel, path: &std::path::Path) -> Result<Loaded> {
        let backend = backend()?;
        let started = Instant::now();
        let params = LlamaModelParams::default();
        // Apple Silicon shares memory with its GPU, so every layer goes to Metal.
        #[cfg(all(target_arch = "aarch64", target_vendor = "apple"))]
        let params = params.with_n_gpu_layers(1000);
        let loaded = LlamaModel::load_from_file(backend, path, &params)
            .map_err(|e| fail("The chat model could not be loaded", e))?;
        log!("llm: loaded {} in {} ms", model.id, started.elapsed().as_millis());
        Ok(Loaded { id: model.id, model: loaded })
    }

    /// The chat prompt, ending in the assistant header and the no-thinking prefill.
    fn prompt(model: &LlamaModel, req: &Request, user: &str) -> Result<String> {
        let template = model
            .chat_template(None)
            .or_else(|_| LlamaChatTemplate::new("chatml"))
            .map_err(|e| fail("The chat model has no usable chat template", e))?;
        let message = |role: &str, content: &str| {
            LlamaChatMessage::new(role.into(), content.into()).map_err(|e| fail("Bad prompt", e))
        };
        let mut messages = vec![message("system", req.system)?];
        for (question, answer) in req.shots {
            messages.push(message("user", question)?);
            messages.push(message("assistant", answer)?);
        }
        messages.push(message("user", user)?);
        let mut prompt =
            model.apply_chat_template(&template, &messages, true).map_err(|e| fail("Bad chat template", e))?;
        prompt.push_str(NO_THINK_PREFILL);
        Ok(prompt)
    }

    /// The prompt's tokens, cutting `user` from the end until prompt and reply fit `MAX_CONTEXT`.
    fn tokens(model: &LlamaModel, req: &Request, max_tokens: u32) -> Result<Vec<LlamaToken>> {
        let budget = (MAX_CONTEXT - max_tokens - 16) as usize;
        let mut user = req.user.to_string();
        for _ in 0..4 {
            let tokens = model.vocab().tokenize(prompt(model, req, &user)?.as_bytes(), true, true);
            if tokens.len() <= budget {
                return Ok(tokens);
            }
            let chars = user.chars().count();
            let keep = chars * budget / tokens.len() * 9 / 10;
            log!("llm: input too long ({} tokens), keeping {keep} of {chars} chars", tokens.len());
            user = truncate_chars(&user, keep);
        }
        Err(LlmError("The text is too long for the chat model.".into()))
    }

    pub fn generate(loaded: &Loaded, req: &Request, cancelled: &dyn Fn() -> bool) -> Result<String> {
        let backend = backend()?;
        let model = &loaded.model;
        let max_tokens = req.max_tokens.clamp(16, MAX_CONTEXT / 2);
        let prompt = tokens(model, req, max_tokens)?;
        let n_ctx = context_size(prompt.len(), max_tokens);
        let threads = default_threads();
        let params = LlamaContextParams::default()
            .with_n_ctx(NonZeroU32::new(n_ctx))
            .with_n_batch(n_ctx)
            .with_n_threads(threads)
            .with_n_threads_batch(threads);
        let mut ctx =
            model.new_context(backend, params).map_err(|e| fail("Not enough memory for the chat model", e))?;

        let mut batch = LlamaBatch::new(n_ctx as usize, 1);
        let last = prompt.len() - 1;
        for (i, &token) in prompt.iter().enumerate() {
            batch.add(token, i as i32, &[0], i == last).map_err(|e| fail("Prompt", e))?;
        }
        ctx.decode(&mut batch).map_err(|e| fail("The chat model failed", e))?;

        // Qwen3's recommended settings for answers without thinking.
        let mut sampler = LlamaSampler::chain_simple([
            LlamaSampler::top_k(20),
            LlamaSampler::top_p(0.8, 1),
            LlamaSampler::min_p(0.0, 1),
            LlamaSampler::temp(req.temperature.unwrap_or(0.7)),
            LlamaSampler::dist(0x5eed),
        ]);
        let vocab = model.vocab();
        let mut out = Vec::new();
        let start = prompt.len() as i32;
        let end = (start + max_tokens as i32).min(n_ctx as i32);
        for pos in start..end {
            if cancelled() {
                return Ok(String::new());
            }
            let token = sampler.sample(&ctx, batch.n_tokens() - 1);
            sampler.accept(token);
            if vocab.is_eog(token) {
                break;
            }
            vocab.token_to_piece_into(token, &mut out, true, None);
            batch.clear();
            batch.add(token, pos, &[0], true).map_err(|e| fail("Generation", e))?;
            ctx.decode(&mut batch).map_err(|e| fail("The chat model failed", e))?;
        }
        Ok(strip_thinking(&String::from_utf8_lossy(&out)))
    }
}

#[cfg(not(feature = "local-llm"))]
mod engine {
    use super::*;

    pub struct Loaded {
        pub id: &'static str,
    }

    const MISSING: &str = "This build has no built-in chat model; choose Lemonade in Settings.";

    pub fn backend() -> Result<()> {
        Err(LlmError(MISSING.into()))
    }

    pub fn load(_: &'static ChatModel, _: &std::path::Path) -> Result<Loaded> {
        Err(LlmError(MISSING.into()))
    }

    pub fn generate(_: &Loaded, _: &Request, _: &dyn Fn() -> bool) -> Result<String> {
        Err(LlmError(MISSING.into()))
    }
}

/// Starts llama.cpp (and on a Mac compiles its GPU kernels) without loading a model.
pub fn warm_up() {
    let started = Instant::now();
    match engine::backend() {
        Ok(_) => crate::log!("llm: llama.cpp ready in {} ms", started.elapsed().as_millis()),
        Err(e) => crate::log!("llm: {e}"),
    }
}

struct State {
    loaded: engine::Loaded,
    last_used: Instant,
}

/// The loaded chat model, shared by every request.
#[derive(Default)]
pub struct Engine {
    state: Mutex<Option<State>>,
}

impl Engine {
    pub fn is_loaded(&self) -> bool {
        self.state.lock().map(|s| s.is_some()).unwrap_or(false)
    }

    /// Writes a reply to the request with the model `model_id`, downloading and loading it first if
    /// needed. A cancelled call returns an empty reply.
    pub fn request(
        &self,
        model_id: &str,
        req: &Request,
        cancelled: &dyn Fn() -> bool,
        on_progress: &mut dyn FnMut(Progress),
    ) -> Result<String> {
        let model = find_model(model_id);
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.as_ref().is_some_and(|s| s.loaded.id != model.id) {
            *state = None;
        }
        if state.is_none() {
            let path = ensure_model(model, cancelled, &mut |f| on_progress(Progress::Downloading(f)))?;
            if cancelled() {
                return Ok(String::new());
            }
            on_progress(Progress::Loading);
            *state = Some(State { loaded: engine::load(model, &path)?, last_used: Instant::now() });
        }
        on_progress(Progress::Writing);
        let s = state.as_mut().expect("loaded above");
        let reply = engine::generate(&s.loaded, req, cancelled);
        s.last_used = Instant::now();
        reply
    }

    /// Drops the model once it has been idle for `idle`. Never waits on a running request.
    /// Returns when to check again, if a model is still loaded.
    pub fn unload_if_idle(&self, idle: Duration) -> Option<Instant> {
        let mut state = self.state.try_lock().ok()?;
        let due = state.as_ref()?.last_used + idle;
        if Instant::now() >= due {
            crate::log!("llm: unloading the chat model after {} s idle", idle.as_secs());
            *state = None;
            return None;
        }
        Some(due)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn model_table_is_sane() {
        let mut ids: Vec<_> = CHAT_MODELS.iter().map(|m| m.id).collect();
        ids.dedup();
        assert_eq!(ids.len(), CHAT_MODELS.len());
        for m in &CHAT_MODELS {
            assert_eq!(m.file.sha256.len(), 64);
            assert!(m.file.sha256.chars().all(|c| c.is_ascii_hexdigit()));
            assert_eq!(m.revision.len(), 40);
            assert!(m.file.size > 100_000_000);
            assert!(m.file.name.ends_with(".gguf"));
            assert!(model_path(m).ends_with(m.file.name));
            assert!(m.memory_estimate() > m.file.size);
        }
        assert_eq!(CHAT_MODELS[0].id, DEFAULT_MODEL_ID);
        assert_eq!(find_model("nope").id, DEFAULT_MODEL_ID);
        assert_eq!(find_model(" QWEN3-0.6B ").id, "qwen3-0.6b");
    }

    #[test]
    fn estimates_memory() {
        assert_eq!(size_label(find_model("qwen3-1.7b").memory_estimate()), "1.7 GB");
        assert_eq!(size_label(find_model("qwen3-0.6b").memory_estimate()), "1.0 GB");
        assert_eq!(size_label(396_705_472), "397 MB");
        assert_eq!(size_label(1_107_409_472), "1.1 GB");
    }

    #[test]
    fn strips_thinking() {
        assert_eq!(strip_thinking("<think>hmm</think>\n\nThe answer."), "The answer.");
        assert_eq!(strip_thinking("  Plain.  "), "Plain.");
        assert_eq!(strip_thinking("<think>\n\n</think>"), "");
    }

    #[test]
    fn truncates_on_char_boundaries() {
        assert_eq!(truncate_chars("héllo wörld", 5), "héllo…");
        assert_eq!(truncate_chars("short", 10), "short");
        assert_eq!(truncate_chars("ab ", 2), "ab…");
    }

    #[test]
    fn sizes_contexts() {
        assert_eq!(context_size(100, 128), 256);
        assert_eq!(context_size(300, 384), 768);
        assert_eq!(context_size(10_000, 384), MAX_CONTEXT);
        assert!((2..=8).contains(&default_threads()));
        assert!(NO_THINK_PREFILL.starts_with("<think>") && NO_THINK_PREFILL.ends_with("\n\n"));
    }

    #[test]
    fn titles_failures() {
        assert_eq!(failure_title("Could not download x: 404", "Summary"), "Chat model download failed");
        assert_eq!(failure_title("x.gguf failed its checksum.", "Explain"), "Chat model download failed");
        assert_eq!(
            failure_title("Not enough memory for the chat model: alloc", "Explain"),
            "Not enough memory for the chat model"
        );
        assert_eq!(failure_title("boom", "Explain"), "Explain failed");
        assert_eq!(failure_title("boom", "Summary"), "Summary failed");
        assert_eq!(failure_title("boom", "ELI5"), "ELI5 failed");
    }

    #[test]
    fn idle_engine_has_nothing_to_unload() {
        let engine = Engine::default();
        assert!(!engine.is_loaded());
        assert_eq!(engine.unload_if_idle(Duration::ZERO), None);
    }

    /// Runs a real Summary with the default model, if it has been downloaded, and reports how
    /// long it took and the process's peak memory. `cargo test real_chat -- --ignored --nocapture`
    #[test]
    #[ignore]
    #[cfg(feature = "local-llm")]
    fn real_chat_when_model_is_present() {
        let only = std::env::var("DT_LLM_MODEL").unwrap_or_default();
        for model in CHAT_MODELS.iter().filter(|m| only.is_empty() || m.id == only) {
            if !is_downloaded(model) {
                eprintln!("{} not downloaded; skipping", model.id);
                continue;
            }
            let engine = Engine::default();
            let text = "The James Webb Space Telescope is a space telescope designed to conduct infrared astronomy. \
Its high-resolution and high-sensitivity instruments allow it to view objects too old, distant, or faint for the \
Hubble Space Telescope. This enables investigations across many fields of astronomy and cosmology, such as \
observation of the first stars and the formation of the first galaxies.";
            let started = Instant::now();
            let reply = engine
                .request(
                    model.id,
                    &Request::new(crate::text::SUMMARY_PROMPT, text, crate::text::SUMMARY_MAX_TOKENS),
                    &|| false,
                    &mut |_| {},
                )
                .unwrap();
            let first = started.elapsed();
            let started = Instant::now();
            let explain = crate::text::explain_prompt(crate::settings::ExplainAudience::General, 2);
            let again = engine
                .request(model.id, &Request::new(&explain, "infrared astronomy", 192), &|| false, &mut |_| {})
                .unwrap();
            let warm = started.elapsed();
            // About 3,500 tokens: close to the largest context a request can use.
            let long = text.repeat(40);
            let started = Instant::now();
            let long_reply = engine
                .request(
                    model.id,
                    &Request::new(crate::text::SUMMARY_PROMPT, &long, crate::text::SUMMARY_MAX_TOKENS),
                    &|| false,
                    &mut |_| {},
                )
                .unwrap();
            eprintln!(
                "{}: {first:?} (with load) / {warm:?} warm / {:?} long, RSS {}, estimate {}\n  summary: {reply}\n  explain: {again}\n  long: {long_reply}",
                model.id,
                started.elapsed(),
                size_label(peak_rss()),
                size_label(model.memory_estimate())
            );
            assert!(!reply.is_empty() && !reply.contains("<think>"));
            assert!(!again.is_empty() && !again.contains("<think>"));
            assert!(engine.unload_if_idle(Duration::ZERO).is_none());
            assert!(!engine.is_loaded());
        }
    }

    #[cfg(feature = "local-llm")]
    fn peak_rss() -> u64 {
        let out = std::process::Command::new("ps").args(["-o", "rss=", "-p", &std::process::id().to_string()]).output();
        out.ok().and_then(|o| String::from_utf8_lossy(&o.stdout).trim().parse::<u64>().ok()).unwrap_or(0) * 1024
    }
}
