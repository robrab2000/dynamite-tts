using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DynamiteTts.Services;
using Xunit;

namespace DynamiteTts.Tests;

public class LocalTtsServiceTests
{
    [Fact]
    public void ResolveVoice_MapsCommonNames()
    {
        var voice1 = LocalTtsService.ResolveVoice("coral");
        Assert.NotNull(voice1);
        Assert.Equal("af_heart", voice1.Name);

        var voice2 = LocalTtsService.ResolveVoice("alloy");
        Assert.NotNull(voice2);
        Assert.Equal("af_sarah", voice2.Name);

        var voice3 = LocalTtsService.ResolveVoice("bm_george");
        Assert.NotNull(voice3);
        Assert.Equal("bm_george", voice3.Name);
    }

    [Fact]
    public async Task GenerateSpeechWavAsync_EmptyText_ReturnsEmptyArray()
    {
        using var service = new LocalTtsService();
        var result = await service.GenerateSpeechWavAsync("", "af_heart", 1.0);
        Assert.Empty(result);
    }
}
