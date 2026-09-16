using System;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class LemonadeDependencyServiceTests
{
    [Fact]
    public void DefaultChatModelId_IsSmallCpuFriendlyModel()
    {
        Assert.Equal("Qwen2.5-0.5B-Instruct", LemonadeDependencyService.DefaultChatModelId);
        Assert.False(LemonadeChatClient.IsLikelyTtsModel(LemonadeDependencyService.DefaultChatModelId));
    }

    [Theory]
    [InlineData("http://localhost:13305/api/v1/chat/completions", 13305)]
    [InlineData("http://127.0.0.1:8000/v1/models", 8000)]
    [InlineData("localhost:13305", 13305)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryGetPort_ParsesEndpoint(string? endpoint, int? expected)
    {
        Assert.Equal(expected, LemonadeDependencyService.TryGetPort(endpoint));
    }
}
