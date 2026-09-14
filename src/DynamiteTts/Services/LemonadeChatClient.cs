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

    public async Task<string> SummarizeAsync(
        string chatEndpoint,
        string? model,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

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
                available + " Summary mode needs a chat LLM in Lemonade Server " +
                "(for example: lemonade pull Qwen2.5-0.5B-Instruct, then lemonade run Qwen2.5-0.5B-Instruct).");
        }

        if (!string.IsNullOrWhiteSpace(requestedModel) &&
            !string.Equals(requestedModel, resolvedModel, StringComparison.OrdinalIgnoreCase) &&
            IsLikelyTtsModel(requestedModel))
        {
            AppLog.Warn($"Summary model '{requestedModel}' is TTS-only; using chat model '{resolvedModel}' instead.");
        }

        var payload = new Dictionary<string, object>
        {
            ["model"] = resolvedModel,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = DefaultSystemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = text }
            },
            ["stream"] = false
        };

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
                        $"Model '{resolvedModel}' cannot summarize. Choose a chat LLM in Settings → Summary Model, " +
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
                "Summary mode needs a local Lemonade LLM. Start Lemonade Server from Settings, then try again.",
                ex);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Lemonade Server at {resolvedChatUrl} timed out while summarizing.");
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
