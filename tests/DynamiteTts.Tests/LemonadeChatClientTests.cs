using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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
    }
}
