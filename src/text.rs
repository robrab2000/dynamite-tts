//! Text cleanup for spoken delivery, sentence chunking, and the Summary / Explain prompts.

use std::sync::LazyLock;

use regex::{Captures, Regex};

use crate::settings::ExplainAudience;

static LINK: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\[([^\]]+)\]\((?:https?://[^\s\)]+|[^\)]+)\)").unwrap());
static URL: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?:https?://|www\.)[^\s<>()]+").unwrap());
static FENCE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"```[a-zA-Z0-9_\-]*\n?").unwrap());
static HEADER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?m)^[ \t]*#{1,6}[ \t]+").unwrap());
static BULLET: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?m)^[ \t]*[-*+•][ \t]+").unwrap());
// The regex crate has no backreferences, so each emphasis marker is its own alternative.
static EMPHASIS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\*\*(.*?)\*\*|__(.*?)__|\*(.*?)\*|_(.*?)_").unwrap());
static SPACES: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[ \t]+").unwrap());
static BLANKS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\n{3,}").unwrap());

fn host_name(raw: &str) -> String {
    let prefixed =
        if raw.to_ascii_lowercase().starts_with("http") { raw.to_string() } else { format!("https://{raw}") };
    url::Url::parse(&prefixed)
        .ok()
        .and_then(|u| u.host_str().map(|h| h.replace("www.", "")))
        .filter(|h| !h.is_empty())
        .unwrap_or_else(|| "link".into())
}

/// Strips Markdown and turns URLs into "link to example.com" so the text reads well aloud.
pub fn sanitize(text: &str) -> String {
    let text = text.replace("\r\n", "\n").replace('\r', "\n");
    let text = LINK.replace_all(&text, "$1");
    let text = URL.replace_all(&text, |c: &Captures| format!("link to {}", host_name(&c[0])));
    let text = FENCE.replace_all(&text, "");
    let text = text.replace("```", "").replace('`', "");
    let text = HEADER.replace_all(&text, "");
    let text = BULLET.replace_all(&text, "");
    let text = EMPHASIS
        .replace_all(&text, |c: &Captures| (1..=4).find_map(|i| c.get(i)).map_or("", |m| m.as_str()).to_string());
    let text = SPACES.replace_all(&text, " ");
    let text = BLANKS.replace_all(&text, "\n\n");
    text.trim().to_string()
}

pub const MAX_CHUNK_CHARS: usize = 500;

/// Splits text into pieces of at most [`MAX_CHUNK_CHARS`] characters at sentence ends and line
/// breaks, then at spaces. Text that fits is returned whole.
pub fn chunks(text: &str) -> Vec<String> {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return Vec::new();
    }
    if trimmed.chars().count() <= MAX_CHUNK_CHARS {
        return vec![trimmed.to_string()];
    }
    let mut out = Vec::new();
    let mut current = String::new();
    for sentence in sentences(trimmed) {
        let part = sentence.trim();
        if part.is_empty() {
            continue;
        }
        let len = part.chars().count();
        if len > MAX_CHUNK_CHARS {
            if !current.is_empty() {
                out.push(std::mem::take(&mut current));
            }
            out.extend(split_words(part));
            continue;
        }
        if !current.is_empty() && current.chars().count() + len + 1 > MAX_CHUNK_CHARS {
            out.push(std::mem::take(&mut current));
        }
        if !current.is_empty() {
            current.push(' ');
        }
        current.push_str(part);
    }
    if !current.is_empty() {
        out.push(current);
    }
    out
}

/// Pieces ending at `.`, `!` or `?` followed by whitespace, or at a line break.
fn sentences(text: &str) -> Vec<&str> {
    let mut out = Vec::new();
    let mut start = 0;
    let mut chars = text.char_indices().peekable();
    while let Some((i, c)) = chars.next() {
        let next_is_space = chars.peek().is_some_and(|(_, n)| n.is_whitespace());
        if c == '\n' || (matches!(c, '.' | '!' | '?') && next_is_space) {
            let end = i + c.len_utf8();
            out.push(&text[start..end]);
            start = end;
        }
    }
    out.push(&text[start..]);
    out
}

fn split_words(sentence: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut current = String::new();
    for word in sentence.split(' ').filter(|w| !w.is_empty()) {
        if !current.is_empty() && current.chars().count() + word.chars().count() + 1 > MAX_CHUNK_CHARS {
            out.push(std::mem::take(&mut current));
        }
        if !current.is_empty() {
            current.push(' ');
        }
        current.push_str(word);
    }
    if !current.is_empty() {
        out.push(current);
    }
    out
}

pub const SUMMARY_PROMPT: &str = "You summarize text for spoken delivery. Reply with a concise plain-prose summary only. \
No markdown, bullets, headings, titles, or preamble. Do not say that you are summarizing.";

pub fn explain_prompt(audience: ExplainAudience, sentence_count: u32) -> String {
    let count = sentence_count.clamp(1, 8);
    let register = match audience {
        ExplainAudience::General => "Use plain language for a general listener; avoid unnecessary jargon.",
        ExplainAudience::Technical => {
            "Write for a technical peer: keep accurate jargon when useful; do not oversimplify. \
When a term has both an everyday or scientific sense and a computing sense, prefer the computing sense \
unless the surrounding text clearly points elsewhere."
        }
    };
    format!(
        "You explain highlighted text for spoken delivery. \
Open by naming the selection, then explain it. \
If the selection is a short term or phrase, briefly define or clarify it. \
If it is longer, briefly explain what it means and why it matters. \
When surrounding text is provided, you MUST pick the meaning that fits that surrounding text. \
Do not fall back to a more common dictionary sense that contradicts the surroundings \
(for example, in 'browsers/Electron UIs', Electron is the desktop app framework, not the subatomic particle). \
Explain only the highlight, not the whole surrounding passage. \
{register} \
Aim for about {count} sentences. \
Ground the explanation in the selection and any provided surrounding text; do not invent outside facts. \
If you are unsure, say so briefly and still give the best explanation you can. \
Reply with concise plain prose only. No markdown, bullets, headings, titles, or preamble. \
Do not say that you are explaining."
    )
}

/// Room for about `sentences` sentences of explanation.
pub fn explain_max_tokens(sentences: u32) -> u32 {
    (sentences * 96).max(128)
}

/// Selections at or below this length are short enough that surrounding text helps Explain.
pub const SHORT_SELECTION_CHARS: usize = 80;
pub const MAX_CONTEXT_CHARS: usize = 1500;

pub fn is_short_selection(text: &str) -> bool {
    let t = text.trim();
    !t.is_empty() && t.chars().count() <= SHORT_SELECTION_CHARS
}

/// Surrounding text, only when it adds something beyond the selection itself.
pub fn normalize_context(selection: &str, surrounding: Option<&str>) -> Option<String> {
    let selection = selection.trim();
    let context = surrounding?.trim();
    if context.is_empty() || context.to_lowercase() == selection.to_lowercase() {
        return None;
    }
    if context.chars().count() <= selection.chars().count() + 8 {
        return None;
    }
    if context.chars().count() > MAX_CONTEXT_CHARS {
        let cut: String = context.chars().take(MAX_CONTEXT_CHARS).collect();
        return Some(format!("{}…", cut.trim_end()));
    }
    Some(context.to_string())
}

pub fn explain_user_message(selection: &str, surrounding: Option<&str>) -> String {
    let trimmed = selection.trim();
    match normalize_context(trimmed, surrounding) {
        None => trimmed.to_string(),
        // Lead with the passage so small models settle the sense before seeing the bare term.
        Some(context) => format!(
            "Surrounding text:\n{context}\n\nWithin that text, explain only this highlighted word or phrase (not the whole passage):\n{trimmed}"
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_markdown() {
        assert_eq!(sanitize("# Title\n\n- **bold** and _it_\n* `code`"), "Title\n\nbold and it\ncode");
        assert_eq!(sanitize("```rust\nlet x = 1;\n```"), "let x = 1;");
        assert_eq!(sanitize("see [the docs](https://example.com/a)"), "see the docs");
    }

    #[test]
    fn speaks_urls_as_hosts() {
        assert_eq!(sanitize("go to https://www.example.com/path?q=1 now"), "go to link to example.com now");
        assert_eq!(sanitize("www.rust-lang.org"), "link to rust-lang.org");
    }

    #[test]
    fn collapses_whitespace() {
        assert_eq!(sanitize("a  \t b\r\n\r\n\r\n\r\nc"), "a b\n\nc");
        assert_eq!(sanitize("   "), "");
    }

    #[test]
    fn short_text_is_one_chunk() {
        assert_eq!(chunks("  Hello there.  "), vec!["Hello there."]);
        assert!(chunks(" \n ").is_empty());
    }

    #[test]
    fn long_text_splits_at_sentences_within_the_limit() {
        let sentence = "This sentence is exactly fifty characters long ok. ";
        let text = sentence.repeat(25);
        let parts = chunks(&text);
        assert!(parts.len() > 1);
        assert!(parts.iter().all(|p| p.chars().count() <= MAX_CHUNK_CHARS));
        assert!(parts.iter().all(|p| p.ends_with('.')));
        assert_eq!(parts.join(" "), text.trim());
    }

    #[test]
    fn an_overlong_sentence_splits_at_words() {
        let text = "word ".repeat(300);
        let parts = chunks(&text);
        assert!(parts.len() >= 3);
        assert!(parts.iter().all(|p| p.chars().count() <= MAX_CHUNK_CHARS));
    }

    #[test]
    fn explain_message_adds_useful_context_only() {
        assert_eq!(explain_user_message(" Electron ", None), "Electron");
        assert_eq!(explain_user_message("Electron", Some("electron")), "Electron");
        assert_eq!(explain_user_message("Electron", Some("Electron app")), "Electron");
        let msg = explain_user_message("Electron", Some("Most browsers/Electron UIs render with Chromium."));
        assert!(msg.starts_with("Surrounding text:\nMost browsers"));
        assert!(msg.ends_with("\nElectron"));
        let long = "x".repeat(2000);
        assert!(normalize_context("a", Some(&long)).unwrap().ends_with('…'));
    }

    #[test]
    fn explain_prompt_clamps_and_registers() {
        assert!(explain_prompt(ExplainAudience::General, 0).contains("about 1 sentences"));
        assert!(explain_prompt(ExplainAudience::Technical, 20).contains("about 8 sentences"));
        assert!(explain_prompt(ExplainAudience::Technical, 3).contains("technical peer"));
        assert_eq!(explain_max_tokens(1), 128);
        assert_eq!(explain_max_tokens(3), 288);
    }
}
