//! Text cleanup for spoken delivery, sentence chunking, and the Summary / Explain / ELI5 prompts.

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
static WORD_UNDERSCORE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"([\p{L}\p{N}])_([\p{L}\p{N}])").unwrap());
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
    // An underscore inside a word (snake_case) is not emphasis, and reads best as a space. The
    // second pass catches the ones the first skips in a_b_c, where the matches would overlap.
    let text = WORD_UNDERSCORE.replace_all(&text, "$1 $2");
    let text = WORD_UNDERSCORE.replace_all(&text, "$1 $2");
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

/// Room for a short spoken summary from the built-in model (Lemonade sets no limit).
pub const SUMMARY_MAX_TOKENS: u32 = 384;

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

/// ELI5 runs in two steps when there is surrounding text. A small model asked to explain a
/// word that has a common everyday sense ("Lemonade") explains that sense even when the passage
/// says otherwise, so the first step only works out what the highlight is in the passage, and
/// the second explains that meaning. Worked examples steer a small model far better than rules.
pub const ELI5_MEANING_PROMPT: &str = "You work out what a highlighted word or phrase is in a passage. \
Everyday words are often names of apps, programs, servers, products or companies, so look at the other words in the passage. \
Reply in two lines: CLUES lists words copied from the passage that say what the highlight does, has or is part of; \
MEANING says what the highlight is in this passage, in a few plain words.";

/// (passage message, reply) pairs sent before the real passage.
pub const ELI5_MEANING_SHOTS: [(&str, &str); 3] = [
    (
        "Passage:\nPost the build link in Slack so the whole team sees it before the release.\n\nHighlighted: Slack",
        "CLUES: post, link, team, release\nMEANING: a chat app that work teams use to send messages",
    ),
    (
        "Passage:\nApache was still listening on port 8080 after the update, with the old config loaded.\n\nHighlighted: Apache",
        "CLUES: listening, port 8080, update, config loaded\nMEANING: a web server program on a computer",
    ),
    (
        "Passage:\nThe jaguar slept high in the tree after a long night of hunting.\n\nHighlighted: jaguar",
        "CLUES: slept, tree, hunting\nMEANING: a big wild cat",
    ),
];

/// Low, so the same passage settles on the same meaning each time.
pub const ELI5_MEANING_TEMPERATURE: f32 = 0.2;
pub const ELI5_MEANING_MAX_TOKENS: u32 = 60;

pub fn eli5_meaning_message(selection: &str, surrounding: &str) -> String {
    format!("Passage:\n{}\n\nHighlighted: {}", surrounding.trim(), selection.trim())
}

/// The MEANING line of the first step's reply, when it gave a usable one.
pub fn parse_meaning(reply: &str) -> Option<String> {
    let line = reply.lines().find_map(|l| l.trim().strip_prefix("MEANING:"))?;
    let meaning = line.trim().trim_matches(|c: char| c == '"' || c == '\'' || c == '.' || c.is_whitespace());
    (!meaning.is_empty() && meaning.chars().count() <= 120).then(|| meaning.to_string())
}

pub const ELI5_PROMPT: &str = "You explain things to a curious five-year-old, for spoken delivery. \
You are told what the highlighted word or phrase means in its passage; explain that meaning and no other, \
even if the word has a more common everyday meaning. \
Use about four short sentences with simple everyday words. \
Leave out jargon; if you must use a hard word, say what it means right away. \
Its name may also be an everyday word; if so, do not mention that everyday thing at all, not even in a comparison. \
You may use one comparison to something from everyday life to show what it does. \
Explain only the highlight, not the whole passage. \
Reply with plain prose only. No markdown, bullets, headings, titles, or preamble. \
Do not mention five-year-olds, children, or that you are simplifying.";

/// Room for about four short sentences.
pub const ELI5_MAX_TOKENS: u32 = 320;
/// Small models often run on; ELI5 speaks at most this many sentences.
pub const ELI5_MAX_SENTENCES: usize = 4;

/// The first `n` sentences of `text`, joined with spaces.
pub fn first_sentences(text: &str, n: usize) -> String {
    let parts: Vec<&str> = sentences(text.trim()).into_iter().map(str::trim).filter(|s| !s.is_empty()).collect();
    parts.into_iter().take(n).collect::<Vec<_>>().join(" ")
}

/// The paragraphs around the selection, at most [`MAX_CONTEXT_CHARS`], for ELI5.
///
/// `field` is the whole text the selection came from (on macOS, the focused field). The result
/// is the paragraph holding the selection plus one on each side; when that is still too long, a
/// window of it centred on the selection. `None` when the field adds nothing to the selection.
pub fn surrounding_paragraphs(selection: &str, field: Option<&str>) -> Option<String> {
    let selection = selection.trim();
    let field = field?.trim();
    let Some(start) = (!selection.is_empty()).then(|| field.find(selection)).flatten() else {
        return normalize_context(selection, Some(field));
    };
    let end = start + selection.len();
    let paragraphs: Vec<(usize, usize)> = line_spans(field).filter(|&(a, b)| !field[a..b].trim().is_empty()).collect();
    let first = paragraphs.iter().position(|&(_, b)| b > start).unwrap_or(0);
    let last = paragraphs.iter().rposition(|&(a, _)| a < end).unwrap_or(first).max(first);
    let from = paragraphs[first.saturating_sub(1)].0;
    let to = paragraphs[(last + 1).min(paragraphs.len() - 1)].1;
    let passage = &field[from..to];
    let (start, end) = (start - from, end - from);

    let total = passage.chars().count();
    if total <= MAX_CONTEXT_CHARS {
        return normalize_context(selection, Some(passage));
    }
    // Centre a window on the selection, in characters so multi-byte text is never split.
    let sel_start = passage[..start].chars().count();
    let sel_len = passage[start..end].chars().count();
    let spare = MAX_CONTEXT_CHARS.saturating_sub(sel_len);
    let mut lead = sel_start.min(spare / 2);
    let trail = (total - sel_start - sel_len).min(spare - lead);
    lead = sel_start.min(spare - trail);
    let window: String = passage.chars().skip(sel_start - lead).take(lead + sel_len + trail).collect();
    let window = window.trim();
    let before = if sel_start > lead { "…" } else { "" };
    let after = if sel_start + sel_len + trail < total { "…" } else { "" };
    normalize_context(selection, Some(&format!("{before}{window}{after}")))
}

/// Byte ranges of each line, without the line break.
fn line_spans(text: &str) -> impl Iterator<Item = (usize, usize)> + '_ {
    let mut offset = 0;
    text.split('\n').map(move |line| {
        let span = (offset, offset + line.len());
        offset += line.len() + 1;
        span
    })
}

/// The explanation request. With a `meaning` from the first step the reply has to open by
/// stating it, which keeps a small model from drifting back to the everyday sense.
pub fn eli5_user_message(selection: &str, surrounding: Option<&str>, meaning: Option<&str>) -> String {
    let sel = selection.trim();
    match (surrounding.map(str::trim).filter(|c| !c.is_empty()), meaning) {
        (None, _) => format!("Explain this: {sel}"),
        (Some(context), None) => format!("Passage:\n{context}\n\nHighlighted: {sel}\n\nExplain what {sel} is here."),
        (Some(context), Some(meaning)) => format!(
            "Passage:\n{context}\n\nHighlighted: {sel}\nIn this passage, {sel} is {meaning}.\n\n\
Explain {sel}, {meaning}. Begin with exactly: \"{sel} is {meaning}.\" Then keep explaining that."
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
    fn reads_snake_case_as_words() {
        assert_eq!(sanitize("renamed `user_id` to `account_id`"), "renamed user id to account id");
        assert_eq!(sanitize("a_b_c and x_1"), "a b c and x 1");
        assert_eq!(sanitize("_it_ and __init__"), "it and init");
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
    fn eli5_takes_the_paragraphs_around_the_selection() {
        let field = "Intro paragraph.\n\nBefore it.\nThe app uses Electron for its UI.\nAfter it.\n\nFar away.";
        assert_eq!(
            surrounding_paragraphs("Electron", Some(field)).unwrap(),
            "Before it.\nThe app uses Electron for its UI.\nAfter it."
        );
        // A selection across lines keeps every line it touches plus one on each side.
        let spans = surrounding_paragraphs("its UI.\nAfter", Some(field)).unwrap();
        assert_eq!(spans, "Before it.\nThe app uses Electron for its UI.\nAfter it.\n\nFar away.");
        // Nothing beyond the selection itself, or no field at all.
        assert_eq!(surrounding_paragraphs("Electron", Some("Electron")), None);
        assert_eq!(surrounding_paragraphs("Electron", None), None);
        // Not found (the app trimmed or reflowed it): the field as a whole, as for Explain.
        assert_eq!(surrounding_paragraphs("Qubit", Some(field)).unwrap(), field);
    }

    #[test]
    fn eli5_centres_a_long_passage_on_the_selection() {
        let field = format!("{} TARGET {}", "é".repeat(3000), "b".repeat(3000));
        let ctx = surrounding_paragraphs("TARGET", Some(&field)).unwrap();
        assert!(ctx.starts_with('…') && ctx.ends_with('…'));
        assert!(ctx.chars().count() <= MAX_CONTEXT_CHARS + 2);
        let at = ctx.find("TARGET").unwrap();
        let (lead, trail) = (ctx[..at].chars().count(), ctx[at..].chars().count());
        assert!(lead.abs_diff(trail) < 20, "{lead} vs {trail}");
        // Near the start, the spare room goes after the selection.
        let field = format!("TARGET {}", "b".repeat(3000));
        let ctx = surrounding_paragraphs("TARGET", Some(&field)).unwrap();
        assert!(ctx.starts_with("TARGET") && ctx.ends_with('…'));
    }

    #[test]
    fn eli5_message_leads_with_the_passage() {
        assert_eq!(eli5_user_message(" Electron ", None, None), "Explain this: Electron");
        let msg = eli5_user_message("Electron", Some("The app uses Electron."), None);
        assert!(msg.starts_with("Passage:\nThe app uses Electron.\n\nHighlighted: Electron"));
        let msg =
            eli5_user_message("Lemonade", Some("Lemonade was running on port 13305."), Some("a computer program"));
        assert!(msg.contains("In this passage, Lemonade is a computer program."));
        assert!(msg.ends_with("Begin with exactly: \"Lemonade is a computer program.\" Then keep explaining that."));
    }

    #[test]
    fn keeps_the_first_sentences() {
        assert_eq!(first_sentences("One. Two!\nThree? Four. Five.", 4), "One. Two! Three? Four.");
        assert_eq!(first_sentences("  Just one  ", 4), "Just one");
        assert_eq!(first_sentences("", 4), "");
    }

    #[test]
    fn eli5_reads_the_meaning_line() {
        assert_eq!(
            parse_meaning("CLUES: running, install, port\nMEANING: a program that runs on a computer."),
            Some("a program that runs on a computer".into())
        );
        assert_eq!(parse_meaning("MEANING: \"a big wild cat\""), Some("a big wild cat".into()));
        assert_eq!(parse_meaning("Lemonade is a drink."), None);
        assert_eq!(parse_meaning("MEANING:   "), None);
        assert_eq!(parse_meaning(&format!("MEANING: {}", "x".repeat(200))), None);
        assert_eq!(
            eli5_meaning_message(" Lemonade ", " It runs on port 13305. "),
            "Passage:\nIt runs on port 13305.\n\nHighlighted: Lemonade"
        );
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
