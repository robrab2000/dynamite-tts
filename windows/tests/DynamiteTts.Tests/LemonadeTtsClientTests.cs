using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class LemonadeTtsClientTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task GenerateSpeechAsync_SuccessfulResponse_ReturnsAudioBytes()
    {
        var expectedBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x01, 0x02 };
        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Contains("/audio/speech", req.RequestUri?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedBytes)
                };
            }
        };

        using var httpClient = new HttpClient(handler);
        using var client = new LemonadeTtsClient(httpClient);

        var result = await client.GenerateSpeechAsync(
            "http://localhost:13305/api/v1/audio/speech",
            "Test text",
            "kokoro-v1",
            "coral",
            1.0);

        Assert.Equal(expectedBytes, result);
    }

    [Fact]
    public async Task GenerateSpeechAsync_ServerError_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Model loading failed")
            }
        };

        using var httpClient = new HttpClient(handler);
        using var client = new LemonadeTtsClient(httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateSpeechAsync(
                "http://localhost:13305/api/v1/audio/speech",
                "Test text",
                "kokoro-v1",
                "coral",
                1.0));

        Assert.Contains("500", ex.Message);
        Assert.Contains("Model loading failed", ex.Message);
    }

    [Fact]
    public async Task GetModelsAsync_ValidJson_ReturnsDiscoveredModels()
    {
        var json = """
        {
          "object": "list",
          "data": [
            { "id": "kokoro-v1", "recipe": "kokoro", "downloaded": true },
            { "id": "OpenMOSS-TTS", "recipe": "openmoss", "downloaded": true }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                Assert.Contains("/models", req.RequestUri?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        };

        using var httpClient = new HttpClient(handler);
        using var client = new LemonadeTtsClient(httpClient);

        var models = await client.GetModelsAsync("http://localhost:13305/api/v1");

        Assert.Equal(2, models.Count);
        Assert.Equal("kokoro-v1", models[0].Id);
        Assert.Equal("kokoro", models[0].Recipe);
        Assert.True(models[0].Downloaded);
        Assert.Equal("OpenMOSS-TTS", models[1].Id);
    }

    [Fact]
    public async Task TestConnectionAsync_Success_ReturnsTrue()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        };

        using var httpClient = new HttpClient(handler);
        using var client = new LemonadeTtsClient(httpClient);

        var result = await client.TestConnectionAsync("http://localhost:13305");
        Assert.True(result);
    }

    [Fact]
    public async Task TestConnectionAsync_Failure_ReturnsFalse()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };

        using var httpClient = new HttpClient(handler);
        using var client = new LemonadeTtsClient(httpClient);

        var result = await client.TestConnectionAsync("http://localhost:13305");
        Assert.False(result);
    }
}
