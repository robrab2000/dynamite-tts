"""Text cleanup for spoken delivery and the Summary / Explain prompts.

Mirrors macos/DynamiteTTS/Sources/DynamiteTTS/TextAndPrompts.swift so all three
platforms speak and summarize the same way.
"""
from __future__ import annotations

import re
from urllib.parse import urlparse

from .settings import ExplainAudience

_LINK = re.compile(r"\[([^\]]+)\]\((?:https?://[^\s\)]+|[^\)]+)\)")
_URL = re.compile(r"(?:https?://|www\.)[^\s<>()]+")
_FENCE = re.compile(r"```[a-zA-Z0-9_\-]*\n?")
_HEADER = re.compile(r"^\s*#{1,6}\s+", re.MULTILINE)
_BULLET = re.compile(r"^\s*[-*+•]\s+", re.MULTILINE)
_EMPHASIS = re.compile(r"(\*\*|__|\*|_)(.*?)\1")
_SPACES = re.compile(r"[ \t]+")
_BLANKS = re.compile(r"\n{3,}")


def _host_name(raw: str) -> str:
    prefixed = raw if raw.startswith("http") else f"https://{raw}"
    try:
        host = urlparse(prefixed).hostname or ""
    except ValueError:
        host = ""
    host = host.replace("www.", "")
    return host or "link"


def sanitize(text: str) -> str:
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = _LINK.sub(r"\1", text)
    text = _URL.sub(lambda m: f"link to {_host_name(m.group(0))}", text)
    text = _FENCE.sub("", text)
    text = text.replace("```", "").replace("`", "")
    text = _HEADER.sub("", text)
    text = _BULLET.sub("", text)
    text = _EMPHASIS.sub(r"\2", text)
    text = _SPACES.sub(" ", text)
    text = _BLANKS.sub("\n\n", text)
    return text.strip()


SUMMARY_PROMPT = (
    "You summarize text for spoken delivery. Reply with a concise plain-prose summary only. "
    "No markdown, bullets, headings, titles, or preamble. Do not say that you are summarizing."
)


def explain_prompt(audience: ExplainAudience, sentence_count: int) -> str:
    count = min(8, max(1, sentence_count))
    if audience == ExplainAudience.GENERAL:
        register = "Use plain language for a general listener; avoid unnecessary jargon."
    else:
        register = (
            "Write for a technical peer: keep accurate jargon when useful; do not oversimplify. "
            "When a term has both an everyday or scientific sense and a computing sense, prefer the computing sense "
            "unless the surrounding text clearly points elsewhere."
        )
    return (
        "You explain highlighted text for spoken delivery. "
        "Open by naming the selection, then explain it. "
        "If the selection is a short term or phrase, briefly define or clarify it. "
        "If it is longer, briefly explain what it means and why it matters. "
        "When surrounding text is provided, you MUST pick the meaning that fits that surrounding text. "
        "Do not fall back to a more common dictionary sense that contradicts the surroundings "
        "(for example, in 'browsers/Electron UIs', Electron is the desktop app framework, not the subatomic particle). "
        "Explain only the highlight, not the whole surrounding passage. "
        f"{register} "
        f"Aim for about {count} sentences. "
        "Ground the explanation in the selection and any provided surrounding text; do not invent outside facts. "
        "If you are unsure, say so briefly and still give the best explanation you can. "
        "Reply with concise plain prose only. No markdown, bullets, headings, titles, or preamble. "
        "Do not say that you are explaining."
    )


def explain_user_message(selection: str, surrounding: str | None) -> str:
    trimmed = selection.strip()
    context = (surrounding or "").strip()
    if not context:
        return trimmed
    if context.lower() == trimmed.lower():
        return trimmed
    if len(context) <= len(trimmed) + 8:
        return trimmed
    if len(context) > 1500:
        context = context[:1500].strip() + "…"
    return (
        f"Surrounding text:\n{context}\n\n"
        f"Within that text, explain only this highlighted word or phrase (not the whole passage):\n{trimmed}"
    )
