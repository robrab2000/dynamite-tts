import Foundation

enum TextCleanup {
    static func sanitize(_ input: String) -> String {
        var text = input.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        text = replace(text, pattern: #"\[([^\]]+)\]\((?:https?://[^\s\)]+|[^\)]+)\)"#, template: "$1")
        text = replaceURLs(text)
        text = replace(text, pattern: #"```[a-zA-Z0-9_\-]*\n?"#, template: "")
        text = text.replacingOccurrences(of: "```", with: "").replacingOccurrences(of: "`", with: "")
        text = replace(text, pattern: #"(?m)^\s*#{1,6}\s+"#, template: "")
        text = replace(text, pattern: #"(?m)^\s*[-*+•]\s+"#, template: "")
        text = replace(text, pattern: #"(\*\*|__|\*|_)(.*?)\1"#, template: "$2")
        text = replace(text, pattern: #"[ \t]+"#, template: " ")
        text = replace(text, pattern: #"\n{3,}"#, template: "\n\n")
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func replace(_ text: String, pattern: String, template: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return text }
        let range = NSRange(text.startIndex..., in: text)
        return regex.stringByReplacingMatches(in: text, range: range, withTemplate: template)
    }

    private static func replaceURLs(_ text: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: #"(?:https?://|www\.)[^\s<>()]+"#) else { return text }
        let matches = regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).reversed()
        var result = text
        for match in matches {
            guard let range = Range(match.range, in: result) else { continue }
            let raw = String(result[range])
            let host = hostName(raw) ?? "link"
            result.replaceSubrange(range, with: "link to \(host)")
        }
        return result
    }

    private static func hostName(_ raw: String) -> String? {
        let prefixed = raw.hasPrefix("http") ? raw : "https://\(raw)"
        guard let host = URL(string: prefixed)?.host else { return nil }
        return host.replacingOccurrences(of: "www.", with: "")
    }
}

enum Prompts {
    static let summary =
        "You summarize text for spoken delivery. Reply with a concise plain-prose summary only. " +
        "No markdown, bullets, headings, titles, or preamble. Do not say that you are summarizing."

    static func explain(audience: ExplainAudience, sentenceCount: Int) -> String {
        let count = min(8, max(1, sentenceCount))
        let register = audience == .general
            ? "Use plain language for a general listener; avoid unnecessary jargon."
            : "Write for a technical peer: keep accurate jargon when useful; do not oversimplify. " +
              "When a term has both an everyday or scientific sense and a computing sense, prefer the computing sense " +
              "unless the surrounding text clearly points elsewhere."
        return
            "You explain highlighted text for spoken delivery. " +
            "Open by naming the selection, then explain it. " +
            "If the selection is a short term or phrase, briefly define or clarify it. " +
            "If it is longer, briefly explain what it means and why it matters. " +
            "When surrounding text is provided, you MUST pick the meaning that fits that surrounding text. " +
            "Do not fall back to a more common dictionary sense that contradicts the surroundings " +
            "(for example, in 'browsers/Electron UIs', Electron is the desktop app framework, not the subatomic particle). " +
            "Explain only the highlight, not the whole surrounding passage. " +
            register + " " +
            "Aim for about \(count) sentences. " +
            "Ground the explanation in the selection and any provided surrounding text; do not invent outside facts. " +
            "If you are unsure, say so briefly and still give the best explanation you can. " +
            "Reply with concise plain prose only. No markdown, bullets, headings, titles, or preamble. " +
            "Do not say that you are explaining."
    }

    static func explainUserMessage(selection: String, surrounding: String?) -> String {
        let trimmed = selection.trimmingCharacters(in: .whitespacesAndNewlines)
        guard var context = surrounding?.trimmingCharacters(in: .whitespacesAndNewlines), !context.isEmpty else {
            return trimmed
        }
        if context.compare(trimmed, options: .caseInsensitive) == .orderedSame { return trimmed }
        if context.count <= trimmed.count + 8 { return trimmed }
        if context.count > 1500 {
            context = String(context.prefix(1500)).trimmingCharacters(in: .whitespacesAndNewlines) + "…"
        }
        return "Surrounding text:\n\(context)\n\nWithin that text, explain only this highlighted word or phrase (not the whole passage):\n\(trimmed)"
    }
}
