using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Models;

namespace DynamiteTts.Services;

public class LemonadeChatClient : IDisposable
{
    public const string DefaultSystemPrompt =
        "You summarize text for spoken delivery. Reply with a concise plain-prose summary only. " +
        "No markdown, bullets, headings, titles, or preamble. Do not say that you are summarizing.";

    /// <summary>Tokens per requested sentence for a generous max_tokens ceiling (not a tight budget).</summary>
    public const int ExplainTokensPerSentence = 96;

    public const int ExplainMaxTokensFloor = 128;

    private static readonly string[] TtsModelMarkers =
    [
        "kokoro", "tts", "whisper", "speech", "audio", "moss", "openmoss", "stt", "asr"
    ];

    private readonly HttpClient _httpClient;
    private bool _isDisposed;

    public LemonadeChatClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DynamiteTts/1.0");
    }

    public static (string chatUrl, string modelsUrl) ResolveEndpoints(string inputEndpoint)
    {
        var raw = inputEndpoint?.Trim() ?? "http://localhost:13305/api/v1/chat/completions";
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "http://localhost:13305/api/v1/chat/completions";
        }

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        raw = raw.TrimEnd('/');

        string chatUrl;
        string modelsUrl;

        if (raw.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            chatUrl = raw;
            modelsUrl = raw[..^"/chat/completions".Length] + "/models";
        }
        else if (raw.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            modelsUrl = raw;
            chatUrl = raw[..^"/models".Length] + "/chat/completions";
        }
        else if (raw.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase))
        {
            var baseApi = raw[..^"/audio/speech".Length];
            chatUrl = baseApi + "/chat/completions";
            modelsUrl = baseApi + "/models";
        }
        else if (raw.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ||
                 raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            chatUrl = $"{raw}/chat/completions";
            modelsUrl = $"{raw}/models";
        }
        else
        {
            chatUrl = $"{raw}/api/v1/chat/completions";
            modelsUrl = $"{raw}/api/v1/models";
        }

        return (chatUrl, modelsUrl);
    }

    public static bool IsLikelyTtsModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return true;
        var id = modelId.ToLowerInvariant();
        return TtsModelMarkers.Any(marker => id.Contains(marker, StringComparison.Ordinal));
    }

    public static bool IsLikelyTtsModel(TtsModelInfo model)
    {
        if (model == null) return true;
        if (!string.IsNullOrWhiteSpace(model.Recipe) &&
            TtsModelMarkers.Any(m => model.Recipe.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IsLikelyTtsModel(model.Id);
    }

    public static string? PickChatModel(IEnumerable<TtsModelInfo> models, string? preferred)
    {
        var list = models?.ToList() ?? [];
        var chatModels = list.Where(m => !IsLikelyTtsModel(m)).ToList();

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var preferredTrimmed = preferred.Trim();
            var match = chatModels.FirstOrDefault(m =>
                string.Equals(m.Id, preferredTrimmed, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Id;

            // Configured id is absent from /models but does not look like TTS — try it anyway.
            if (!IsLikelyTtsModel(preferredTrimmed) &&
                !list.Any(m => string.Equals(m.Id, preferredTrimmed, StringComparison.OrdinalIgnoreCase)))
            {
                return preferredTrimmed;
            }
        }

        return chatModels.FirstOrDefault()?.Id;
    }

    public static string BuildExplainSystemPrompt(ExplainAudience audience, int sentenceCount)
    {
        var n = AppSettings.ClampExplainSentenceCount(sentenceCount);
        var register = audience == ExplainAudience.General
            ? "Use plain language for a general listener; avoid unnecessary jargon."
            : "Write for a technical peer: keep accurate jargon when useful; do not oversimplify. " +
              "When a term has both an everyday or scientific sense and a computing sense, prefer the computing sense " +
              "unless the surrounding text clearly points elsewhere.";

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
            $"Aim for about {n} sentences. " +
            "Ground the explanation in the selection and any provided surrounding text; do not invent outside facts. " +
            "If you are unsure, say so briefly and still give the best explanation you can. " +
            "Reply with concise plain prose only. No markdown, bullets, headings, titles, or preamble. " +
            "Do not say that you are explaining.";
    }

    public static int GenerousExplainMaxTokens(int sentenceCount)
    {
        var n = AppSettings.ClampExplainSentenceCount(sentenceCount);
        return Math.Max(ExplainMaxTokensFloor, n * ExplainTokensPerSentence);
    }

    public Task<string> SummarizeAsync(
        string chatEndpoint,
        string? model,
        string text,
        CancellationToken cancellationToken = default)
    {
        return CompleteAsync(
            chatEndpoint,
            model,
            text,
            DefaultSystemPrompt,
            maxTokens: null,
            purpose: "summarize",
            cancellationToken);
    }

    public Task<string> ExplainAsync(
        string chatEndpoint,
        string? model,
        string text,
        ExplainAudience audience,
        int sentenceCount,
        string? surroundingContext = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildExplainSystemPrompt(audience, sentenceCount);
        var maxTokens = GenerousExplainMaxTokens(sentenceCount);
        var userMessage = SelectionContextHelper.BuildExplainUserMessage(text, surroundingContext);
        return CompleteAsync(
            chatEndpoint,
            model,
            userMessage,
            prompt,
            maxTokens,
            purpose: "explain",
            cancellationToken);
    }

    private async Task<string> CompleteAsync(
        string chatEndpoint,
        string? model,
        string text,
        string systemPrompt,
        int? maxTokens,
        string purpose,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        var (resolvedChatUrl, _) = ResolveEndpoints(chatEndpoint);
        var requestedModel = model?.Trim() ?? string.Empty;
        var models = await GetModelsAsync(chatEndpoint, cancellationToken);
        var resolvedModel = PickChatModel(models, preferred: requestedModel);

        if (string.IsNullOrWhiteSpace(resolvedModel))
        {
            var available = models.Count == 0
                ? "Lemonade currently has no models loaded."
                : "Lemonade only has TTS models (" + string.Join(", ", models.Select(m => m.Id)) + ").";
            throw new InvalidOperationException(
                available + " Summary and Explain modes need a chat LLM in Lemonade Server. " +
                "Dynamite can install the default model automatically; or run: lemonade pull " +
                LemonadeDependencyService.DefaultChatModelId + ", then lemonade load " +
                LemonadeDependencyService.DefaultChatModelId + ".");
        }

        if (!string.IsNullOrWhiteSpace(requestedModel) &&
            !string.Equals(requestedModel, resolvedModel, StringComparison.OrdinalIgnoreCase) &&
            IsLikelyTtsModel(requestedModel))
        {
            AppLog.Warn($"Chat model '{requestedModel}' is TTS-only; using chat model '{resolvedModel}' instead.");
        }

        var payload = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = text }
            },
            ["stream"] = false
        };

        if (maxTokens is int tokens && tokens > 0)
            payload["max_tokens"] = tokens;

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, resolvedChatUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (body.Contains("does not support chat completion", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Model '{resolvedModel}' cannot {purpose}. Choose a chat LLM in Settings → Chat Model, " +
                        "or pull one in Lemonade (e.g. Qwen2.5-0.5B-Instruct).");
                }

                var errorSummary = body.Length > 200 ? body[..200] + "..." : body;
                throw new InvalidOperationException(
                    $"Lemonade Server returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorSummary}");
            }

            return ParseAssistantContent(body);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Cannot connect to Lemonade Server at {resolvedChatUrl}. " +
                "Lemonade is not running. Dynamite will try to start it automatically; " +
                "or open Lemonade Server from the Start menu, then try again.",
                ex);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Lemonade Server at {resolvedChatUrl} timed out while trying to {purpose}.");
        }
    }

    public static string ParseAssistantContent(string jsonBody)
    {
        using var doc = JsonDocument.Parse(jsonBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentProp))
            {
                var content = contentProp.GetString();
                if (!string.IsNullOrWhiteSpace(content))
                    return content.Trim();
            }

            if (choice.TryGetProperty("text", out var textProp))
            {
                var text = textProp.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }

        throw new InvalidOperationException("Lemonade Server returned an empty or unrecognized chat completion.");
    }

    public async Task<IReadOnlyList<TtsModelInfo>> GetModelsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var (_, resolvedModelsUrl) = ResolveEndpoints(endpoint);
        var models = new List<TtsModelInfo>();

        try
        {
            using var response = await _httpClient.GetAsync(resolvedModelsUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return models;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idProp)) continue;
                    var id = idProp.GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var modelInfo = new TtsModelInfo { Id = id };
                    if (item.TryGetProperty("recipe", out var recipeProp))
                        modelInfo.Recipe = recipeProp.GetString();
                    if (item.TryGetProperty("downloaded", out var dlProp) && dlProp.ValueKind == JsonValueKind.True)
                        modelInfo.Downloaded = true;
                    models.Add(modelInfo);
                }
            }
        }
        catch
        {
            // Non-fatal
        }

        return models;
    }

    public async Task<bool> TestConnectionAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var (_, resolvedModelsUrl) = ResolveEndpoints(endpoint);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _httpClient.GetAsync(resolvedModelsUrl, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _httpClient.Dispose();
    }
}
