using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class LemonadeEndpointResolverTests
{
    [Theory]
    [InlineData("http://localhost:13305/api/v1/audio/speech", "http://localhost:13305/api/v1/audio/speech", "http://localhost:13305/api/v1/models")]
    [InlineData("http://localhost:13305/api/v1", "http://localhost:13305/api/v1/audio/speech", "http://localhost:13305/api/v1/models")]
    [InlineData("http://localhost:13305", "http://localhost:13305/api/v1/audio/speech", "http://localhost:13305/api/v1/models")]
    [InlineData("http://127.0.0.1:8000/v1/audio/speech", "http://127.0.0.1:8000/v1/audio/speech", "http://127.0.0.1:8000/v1/models")]
    public void ResolveEndpoints_VariousFormats_ReturnsCorrectPair(string input, string expectedSpeech, string expectedModels)
    {
        var (speechUrl, modelsUrl) = LemonadeTtsClient.ResolveEndpoints(input);

        Assert.Equal(expectedSpeech, speechUrl);
        Assert.Equal(expectedModels, modelsUrl);
    }
}
