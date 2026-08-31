using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Models;

namespace DynamiteTts.Services;

public class LemonadeTtsClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _isDisposed;

    public LemonadeTtsClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DynamiteTts/1.0");
    }

    public static (string speechUrl, string modelsUrl) ResolveEndpoints(string inputEndpoint)
    {
        var raw = inputEndpoint?.Trim() ?? "http://localhost:13305/api/v1/audio/speech";
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = "http://localhost:13305/api/v1/audio/speech";
        }

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "http://" + raw;
        }

        raw = raw.TrimEnd('/');

        string speechUrl;
        string modelsUrl;

        if (raw.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase))
        {
            speechUrl = raw;
            modelsUrl = raw[..^"/audio/speech".Length] + "/models";
        }
        else if (raw.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            modelsUrl = raw;
            speechUrl = raw[..^"/models".Length] + "/audio/speech";
        }
        else if (raw.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ||
                 raw.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            speechUrl = $"{raw}/audio/speech";
            modelsUrl = $"{raw}/models";
        }
        else
        {
            // Base host like http://localhost:13305
            speechUrl = $"{raw}/api/v1/audio/speech";
            modelsUrl = $"{raw}/api/v1/models";
        }

        return (speechUrl, modelsUrl);
    }

    public async Task<byte[]> GenerateSpeechAsync(
        string speechEndpoint,
        string text,
        string model,
        string voice,
        double speed,
        string responseFormat = "mp3",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speechEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var (resolvedSpeechUrl, _) = ResolveEndpoints(speechEndpoint);

        var payload = new Dictionary<string, object>
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "kokoro-v1" : model,
            ["input"] = text,
            ["voice"] = string.IsNullOrWhiteSpace(voice) ? "coral" : voice,
            ["speed"] = Math.Clamp(speed, 0.25, 4.0),
            ["response_format"] = string.IsNullOrWhiteSpace(responseFormat) ? "mp3" : responseFormat
        };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, resolvedSpeechUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var errorSummary = errorBody.Length > 200 ? errorBody[..200] + "..." : errorBody;
                throw new InvalidOperationException($"Lemonade Server returned {(int)response.StatusCode} {response.ReasonPhrase}: {errorSummary}");
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Cannot connect to Lemonade Server at {resolvedSpeechUrl}. Ensure Lemonade Server is running locally.", ex);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Lemonade Server at {resolvedSpeechUrl} timed out while generating speech.");
        }
    }

    public async Task PrewarmAsync(string endpoint, string model, string voice = "coral")
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await GenerateSpeechAsync(endpoint, ".", model, voice, 1.0, "mp3", cts.Token);
        }
        catch
        {
            // Prewarm failure is non-fatal
        }
    }

    public async Task<IReadOnlyList<TtsModelInfo>> GetModelsAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var (_, resolvedModelsUrl) = ResolveEndpoints(endpoint);
        var models = new List<TtsModelInfo>();

        try
        {
            using var response = await _httpClient.GetAsync(resolvedModelsUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return models;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataElement.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            var modelInfo = new TtsModelInfo { Id = id };
                            if (item.TryGetProperty("recipe", out var recipeProp))
                            {
                                modelInfo.Recipe = recipeProp.GetString();
                            }
                            if (item.TryGetProperty("downloaded", out var dlProp) && dlProp.ValueKind == JsonValueKind.True)
                            {
                                modelInfo.Downloaded = true;
                            }
                            models.Add(modelInfo);
                        }
                    }
                }
            }
        }
        catch
        {
            // Non-fatal, return empty list
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
