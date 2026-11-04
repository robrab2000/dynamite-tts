//! Client for a local Lemonade server: health, starting `lemond`, model load/pull, and chat
//! completions for Summary, Explain and ELI5.

use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use serde_json::{Value, json};

use crate::log;

/// Model ids containing these are not chat models.
const NON_CHAT_MARKERS: &[&str] = &["kokoro", "tts", "whisper", "speech", "audio", "moss", "stt", "asr", "embed"];

#[derive(Debug)]
pub struct LemonadeError(pub String);

impl std::fmt::Display for LemonadeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for LemonadeError {}

type Result<T> = std::result::Result<T, LemonadeError>;

fn err<T>(message: impl Into<String>) -> Result<T> {
    Err(LemonadeError(message.into()))
}

fn shorten(text: &str) -> String {
    let t = text.trim();
    if t.is_empty() {
        return "Lemonade request failed.".into();
    }
    // Lemonade wraps errors as {"error": {"message": ...}}; show just the message.
    let detail = serde_json::from_str::<Value>(t)
        .ok()
        .and_then(|v| {
            v.pointer("/error/message")
                .or_else(|| v.get("error"))
                .or_else(|| v.get("detail"))
                .and_then(Value::as_str)
                .map(str::to_string)
        })
        .unwrap_or_else(|| t.to_string());
    if detail.chars().count() <= 240 { detail } else { format!("{}…", detail.chars().take(240).collect::<String>()) }
}

/// Where Lemonade's server binary may be, most specific first.
fn server_candidates() -> Vec<PathBuf> {
    let mut out =
        vec![crate::paths::data_dir().join("lemonade").join(if cfg!(windows) { "lemond.exe" } else { "lemond" })];
    #[cfg(target_os = "macos")]
    out.extend(
        ["/opt/homebrew/bin/lemond", "/usr/local/bin/lemond", "/Applications/Lemonade.app/Contents/MacOS/lemond"]
            .map(PathBuf::from),
    );
    #[cfg(all(unix, not(target_os = "macos")))]
    out.extend(["/usr/bin/lemond", "/usr/local/bin/lemond", "/opt/lemonade/lemond"].map(PathBuf::from));
    #[cfg(windows)]
    {
        let local = dirs::data_local_dir().unwrap_or_default();
        let pf =
            std::env::var_os("ProgramFiles").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\Program Files"));
        let dirs = [
            local.join(r"lemonade_server\bin"),
            local.join(r"Programs\Lemonade"),
            local.join(r"Programs\LemonadeServer"),
            local.join(r"Programs\lemonade_server\bin"),
            pf.join("Lemonade"),
            pf.join("LemonadeServer"),
            pf.join(r"AMD\Lemonade"),
            pf.join(r"lemonade_server\bin"),
        ];
        for dir in dirs {
            for name in ["LemonadeServer.exe", "lemonade-server.exe", "lemond.exe"] {
                out.push(dir.join(name));
            }
        }
    }
    let names: &[&str] =
        if cfg!(windows) { &["LemonadeServer.exe", "lemonade-server.exe", "lemond.exe"] } else { &["lemond"] };
    if let Some(path) = std::env::var_os("PATH") {
        for dir in std::env::split_paths(&path) {
            for name in names {
                out.push(dir.join(name));
            }
        }
    }
    out
}

pub fn discover_server() -> Option<PathBuf> {
    server_candidates().into_iter().find(|p| p.is_file())
}

fn install_hint() -> &'static str {
    if cfg!(windows) {
        "Lemonade is not installed. Install it with `winget install AMD.LemonadeServer`, then try again."
    } else if cfg!(target_os = "macos") {
        "Lemonade is not installed. Run packaging/macos/install-lemonade.sh, then try again."
    } else {
        "Lemonade is not installed. Run packaging/linux/install-lemonade.sh, then try again."
    }
}

pub struct Client {
    agent: ureq::Agent,
    server: Mutex<Option<Child>>,
}

impl Default for Client {
    fn default() -> Self {
        Self::new()
    }
}

impl Client {
    pub fn new() -> Client {
        let agent: ureq::Agent = ureq::Agent::config_builder()
            .http_status_as_error(false)
            .timeout_connect(Some(Duration::from_secs(5)))
            .user_agent(concat!("DynamiteTTS/", env!("CARGO_PKG_VERSION")))
            .build()
            .into();
        Client { agent, server: Mutex::new(None) }
    }

    fn post(&self, url: &str, body: &Value, timeout: Duration) -> Result<(u16, String)> {
        let response = self
            .agent
            .post(url)
            .config()
            .timeout_global(Some(timeout))
            .build()
            .header("Content-Type", "application/json")
            .send(body.to_string());
        match response {
            Ok(mut r) => {
                let status = r.status().as_u16();
                let text = r.body_mut().with_config().limit(16 * 1024 * 1024).read_to_string().unwrap_or_default();
                Ok((status, text))
            }
            Err(e) => err(format!("Could not reach Lemonade: {e}")),
        }
    }

    fn get(&self, url: &str, timeout: Duration) -> Option<(u16, String)> {
        let mut r = self.agent.get(url).config().timeout_global(Some(timeout)).build().call().ok()?;
        let status = r.status().as_u16();
        Some((status, r.body_mut().read_to_string().unwrap_or_default()))
    }

    pub fn is_healthy(&self, base: &str) -> bool {
        self.get(&format!("{base}/api/v1/health"), Duration::from_secs(2)).is_some_and(|(s, _)| (200..300).contains(&s))
    }

    /// Starts `lemond` (or LemonadeServer.exe) when nothing answers at `base`, and waits for it.
    pub fn ensure_server(&self, base: &str, cancelled: &dyn Fn() -> bool) -> Result<()> {
        if !base.starts_with("http://") && !base.starts_with("https://") {
            return err("The Lemonade address is not a valid URL.");
        }
        if self.is_healthy(base) {
            return Ok(());
        }
        let Some(server) = discover_server() else { return err(install_hint()) };
        let parsed = url::Url::parse(base).map_err(|e| LemonadeError(format!("Bad Lemonade address: {e}")))?;
        let host = parsed.host_str().unwrap_or("127.0.0.1").to_string();
        let port = parsed.port_or_known_default().unwrap_or(13305);
        let log_path = crate::paths::data_dir().join("lemond.log");
        let _ = std::fs::create_dir_all(crate::paths::data_dir());
        let log_file = std::fs::OpenOptions::new().create(true).append(true).open(&log_path).ok();
        let (out, errs) = match &log_file {
            Some(f) => (
                f.try_clone().map(Stdio::from).unwrap_or_else(|_| Stdio::null()),
                f.try_clone().map(Stdio::from).unwrap_or_else(|_| Stdio::null()),
            ),
            None => (Stdio::null(), Stdio::null()),
        };
        let mut command = Command::new(&server);
        let name = server.file_name().and_then(|n| n.to_str()).unwrap_or("").to_ascii_lowercase();
        if name.starts_with("lemonadeserver") || name.starts_with("lemonade-server") {
            // The Windows server binary reads its port from the environment and takes --silent.
            command.arg("--silent").env("LEMONADE_PORT", port.to_string());
        } else {
            command.args(["--host", &host, "--port", &port.to_string(), "--no-broadcast"]);
        }
        command.stdin(Stdio::null()).stdout(out).stderr(errs);
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            command.creation_flags(CREATE_NO_WINDOW);
        }
        log!("starting Lemonade {} on {host}:{port}", server.display());
        let child = command.spawn().map_err(|e| LemonadeError(format!("Lemonade could not be started: {e}")))?;
        *self.server.lock().unwrap_or_else(|e| e.into_inner()) = Some(child);
        let deadline = Instant::now() + Duration::from_secs(30);
        while Instant::now() < deadline {
            if cancelled() || self.is_healthy(base) {
                return Ok(());
            }
            std::thread::sleep(Duration::from_millis(250));
        }
        err(format!("Lemonade did not start on {host}:{port}. See {}.", log_path.display()))
    }

    /// Loads `model`, downloading it first when Lemonade does not have it.
    pub fn ensure_model(&self, base: &str, model: &str, on_download: &dyn Fn()) -> Result<()> {
        let load =
            || self.post(&format!("{base}/api/v1/load"), &json!({"model_name": model}), Duration::from_secs(600));
        let (status, body) = load()?;
        if (200..300).contains(&status) {
            return Ok(());
        }
        if ["not found", "unknown_model", "not downloaded", "not installed"].iter().any(|m| body.contains(m)) {
            on_download();
            self.pull(base, model)?;
            let (status, body) = load()?;
            if (200..300).contains(&status) {
                return Ok(());
            }
            return err(shorten(&body));
        }
        err(shorten(&body))
    }

    pub fn pull(&self, base: &str, model: &str) -> Result<()> {
        let (status, body) =
            self.post(&format!("{base}/api/v1/pull"), &json!({"model_name": model}), Duration::from_secs(3600))?;
        if (200..300).contains(&status) {
            Ok(())
        } else {
            err(format!("Could not download {model}. {}", shorten(&body)))
        }
    }

    pub fn list_models(&self, base: &str) -> Vec<String> {
        let Some((200..=299, body)) = self.get(&format!("{base}/api/v1/models"), Duration::from_secs(10)) else {
            return Vec::new();
        };
        serde_json::from_str::<Value>(&body)
            .ok()
            .and_then(|v| v.get("data").and_then(Value::as_array).cloned())
            .unwrap_or_default()
            .iter()
            .filter_map(|row| row.get("id").and_then(Value::as_str).map(str::to_string))
            .collect()
    }

    pub fn pick_chat_model(&self, base: &str, preferred: &str) -> String {
        pick_chat_model(&self.list_models(base), preferred)
    }

    /// A chat with worked examples: `shots` are (user, assistant) turns sent before `user`.
    pub fn chat_turns(
        &self,
        base: &str,
        model: &str,
        system: &str,
        shots: &[(&str, &str)],
        user: &str,
        max_tokens: Option<u32>,
        temperature: Option<f32>,
    ) -> Result<String> {
        let mut messages = vec![json!({"role": "system", "content": system})];
        for (question, answer) in shots {
            messages.push(json!({"role": "user", "content": question}));
            messages.push(json!({"role": "assistant", "content": answer}));
        }
        messages.push(json!({"role": "user", "content": user}));
        let mut payload = json!({"model": model, "messages": messages, "stream": false});
        if let Some(n) = max_tokens.filter(|n| *n > 0) {
            payload["max_tokens"] = json!(n);
        }
        if let Some(t) = temperature {
            payload["temperature"] = json!(t);
        }
        let (status, body) =
            self.post(&format!("{base}/api/v1/chat/completions"), &payload, Duration::from_secs(180))?;
        if !(200..300).contains(&status) {
            return err(shorten(&body));
        }
        parse_chat(&body)
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        // A lemond we started keeps running for the next launch, like the earlier apps did;
        // only reap it if it has already exited.
        if let Some(child) = self.server.get_mut().ok().and_then(Option::as_mut) {
            let _ = child.try_wait();
        }
    }
}

pub fn pick_chat_model(ids: &[String], preferred: &str) -> String {
    if ids.is_empty() || ids.iter().any(|id| id.eq_ignore_ascii_case(preferred)) {
        return preferred.to_string();
    }
    ids.iter()
        .find(|id| {
            let lower = id.to_lowercase();
            !NON_CHAT_MARKERS.iter().any(|m| lower.contains(m))
        })
        .cloned()
        .unwrap_or_else(|| preferred.to_string())
}

pub fn parse_chat(body: &str) -> Result<String> {
    let root: Value = serde_json::from_str(body)
        .map_err(|_| LemonadeError("Lemonade returned a chat response without text.".into()))?;
    let Some(content) = root.pointer("/choices/0/message/content") else {
        return err("Lemonade returned a chat response without text.");
    };
    let Some(content) = content.as_str() else {
        return err("The chat model returned a reply Dynamite could not read.");
    };
    // Reasoning models may wrap their thinking in <think>…</think>; speak only the answer.
    let content = match content.rfind("</think>") {
        Some(i) => &content[i + "</think>".len()..],
        None => content,
    };
    let content = content.trim();
    if content.is_empty() {
        return err("The chat model returned an empty reply.");
    }
    Ok(content.to_string())
}

/// Short title for a failed request in `mode` (Summary, Explain or ELI5).
pub fn failure_title(message: &str, mode: &str) -> String {
    let m = message.to_lowercase();
    if [
        "could not reach lemonade",
        "could not be started",
        "did not start",
        "actively refused",
        "no connection could be made",
    ]
    .iter()
    .any(|p| m.contains(p))
    {
        return "Lemonade not running".into();
    }
    if m.contains("not installed") {
        return format!("Install Lemonade for {mode}");
    }
    if m.contains("timed out") || m.contains("timeout") {
        return "Lemonade timed out".into();
    }
    format!("{mode} failed")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ids(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn picks_the_preferred_model_or_the_first_chat_model() {
        assert_eq!(pick_chat_model(&[], "Llama"), "Llama");
        assert_eq!(pick_chat_model(&ids(&["kokoro-v1", "llama"]), "Llama"), "Llama");
        assert_eq!(pick_chat_model(&ids(&["kokoro-v1", "Whisper-Base", "Qwen2.5-0.5B"]), "Llama"), "Qwen2.5-0.5B");
        assert_eq!(pick_chat_model(&ids(&["kokoro-v1"]), "Llama"), "Llama");
    }

    #[test]
    fn parses_chat_replies() {
        let ok = r#"{"choices":[{"message":{"content":"  Hello.  "}}]}"#;
        assert_eq!(parse_chat(ok).unwrap(), "Hello.");
        let think = r#"{"choices":[{"message":{"content":"<think>hmm</think>\nAnswer."}}]}"#;
        assert_eq!(parse_chat(think).unwrap(), "Answer.");
        assert!(parse_chat(r#"{"choices":[{"message":{"content":"  "}}]}"#).unwrap_err().0.contains("empty"));
        assert!(parse_chat(r#"{"choices":[]}"#).is_err());
        assert!(parse_chat("not json").is_err());
    }

    #[test]
    fn shortens_lemonade_errors() {
        assert_eq!(shorten(r#"{"error":{"message":"model not found"}}"#), "model not found");
        assert_eq!(shorten(""), "Lemonade request failed.");
        assert_eq!(shorten(&"x".repeat(300)).chars().count(), 241);
    }

    #[test]
    fn titles_failures() {
        assert_eq!(failure_title("Could not reach Lemonade: refused", "Summary"), "Lemonade not running");
        assert_eq!(failure_title("Lemonade is not installed. Run…", "Explain"), "Install Lemonade for Explain");
        assert_eq!(failure_title("boom", "Summary"), "Summary failed");
        assert_eq!(failure_title("boom", "ELI5"), "ELI5 failed");
    }
}
