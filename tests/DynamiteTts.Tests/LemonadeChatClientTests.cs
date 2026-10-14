using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DynamiteTts.Models;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class LemonadeChatClientTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [Theory]
    [InlineData("http://localhost:13305/api/v1/chat/completions", "http://localhost:13305/api/v1/chat/completions", "http://localhost:13305/api/v1/models")]
    [InlineData("http://localhost:13305/api/v1", "http://localhost:13305/api/v1/chat/completions", "http://localhost:13305/api/v1/models")]
    [InlineData("http://localhost:13305", "http://localhost:13305/api/v1/chat/completions", "http://localhost:13305/api/v1/models")]
    [InlineData("http://localhost:13305/api/v1/audio/speech", "http://localhost:13305/api/v1/chat/completions", "http://localhost:13305/api/v1/models")]
    public void ResolveEndpoints_VariousFormats_ReturnsCorrectPair(string input, string expectedChat, string expectedModels)
    {
        var (chatUrl, modelsUrl) = LemonadeChatClient.ResolveEndpoints(input);
        Assert.Equal(expectedChat, chatUrl);
        Assert.Equal(expectedModels, modelsUrl);
    }

    [Fact]
    public void ParseAssistantContent_ReadsMessageContent()
    {
        var json = """{"choices":[{"message":{"role":"assistant","content":"  A short summary.  "}}]}""";
        Assert.Equal("A short summary.", LemonadeChatClient.ParseAssistantContent(json));
    }

    [Fact]
    public void PickChatModel_SkipsTtsModels()
    {
        var models = new List<TtsModelInfo>
        {
            new() { Id = "kokoro-v1" },
            new() { Id = "Qwen2.5-0.5B-Instruct" },
            new() { Id = "whisper-tiny" }
        };

        Assert.Equal("Qwen2.5-0.5B-Instruct", LemonadeChatClient.PickChatModel(models, preferred: null));
        Assert.Equal("Qwen2.5-0.5B-Instruct", LemonadeChatClient.PickChatModel(models, preferred: "Qwen2.5-0.5B-Instruct"));
        Assert.True(LemonadeChatClient.IsLikelyTtsModel("kokoro-v1"));
        Assert.False(LemonadeChatClient.IsLikelyTtsModel("Qwen2.5-0.5B-Instruct"));
    }

    [Fact]
    public void PickChatModel_ReturnsNull_WhenOnlyTtsModelsExist()
    {
        var models = new List<TtsModelInfo> { new() { Id = "kokoro-v1", Recipe = "kokoro" } };
        Assert.Null(LemonadeChatClient.PickChatModel(models, preferred: null));
        Assert.Null(LemonadeChatClient.PickChatModel(models, preferred: "kokoro-v1"));
    }

    [Fact]
    public async Task SummarizeAsync_PostsChatCompletionAndReturnsContent()
    {
        string? postedBody = null;
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/models"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"data":[{"id":"Qwen2.5-0.5B-Instruct"}]}""")
                    };
                }

                postedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"content":"Short take."}}]}""")
                };
            }
        };

        using var http = new HttpClient(handler);
        using var client = new LemonadeChatClient(http);

        var summary = await client.SummarizeAsync(
            "http://localhost:13305/api/v1/chat/completions",
            model: "Qwen2.5-0.5B-Instruct",
            text: "Long article text goes here.");

        Assert.Equal("Short take.", summary);
        Assert.Contains("Long article text goes here.", postedBody);
        Assert.Contains("Qwen2.5-0.5B-Instruct", postedBody);
        Assert.Contains("system", postedBody);
        Assert.DoesNotContain("max_tokens", postedBody);
    }

    [Fact]
    public async Task ExplainAsync_PostsExplainPromptAndMaxTokens()
    {
        string? postedBody = null;
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/models"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"data":[{"id":"Qwen2.5-0.5B-Instruct"}]}""")
                    };
                }

                postedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"content":"Mutex is a lock."}}]}""")
                };
            }
        };

        using var http = new HttpClient(handler);
        using var client = new LemonadeChatClient(http);

        var explanation = await client.ExplainAsync(
            "http://localhost:13305/api/v1/chat/completions",
            model: "Qwen2.5-0.5B-Instruct",
            text: "mutex",
            audience: ExplainAudience.Technical,
            sentenceCount: 3,
            surroundingContext: "Use a mutex to protect the shared queue.");

        Assert.Equal("Mutex is a lock.", explanation);
        Assert.NotNull(postedBody);
        using var doc = JsonDocument.Parse(postedBody!);
        var root = doc.RootElement;
        Assert.Equal(288, root.GetProperty("max_tokens").GetInt32());
        var system = root.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
        var user = root.GetProperty("messages")[1].GetProperty("content").GetString() ?? string.Empty;
        Assert.Contains("Aim for about 3 sentences", system);
        Assert.Contains("technical peer", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("naming the selection", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MUST pick the meaning", system, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Surrounding text:", user);
        Assert.Contains("mutex", user);
        Assert.Contains("shared queue", user);
        Assert.True(user.IndexOf("Surrounding text:", StringComparison.Ordinal) <
                    user.IndexOf("highlighted word or phrase", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ExplainAudience.General, "general listener")]
    [InlineData(ExplainAudience.Technical, "technical peer")]
    public void BuildExplainSystemPrompt_IncludesAudienceAndSentenceCount(ExplainAudience audience, string expectedPhrase)
    {
        var prompt = LemonadeChatClient.BuildExplainSystemPrompt(audience, sentenceCount: 5);
        Assert.Contains(expectedPhrase, prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aim for about 5 sentences", prompt);
    }

    [Fact]
    public void GenerousExplainMaxTokens_UsesFloorAndScale()
    {
        Assert.Equal(128, LemonadeChatClient.GenerousExplainMaxTokens(1));
        Assert.Equal(288, LemonadeChatClient.GenerousExplainMaxTokens(3));
        Assert.Equal(768, LemonadeChatClient.GenerousExplainMaxTokens(8));
        Assert.Equal(768, LemonadeChatClient.GenerousExplainMaxTokens(99));
    }
}
