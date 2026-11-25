using Stanza.Core.Playback;
using Xunit;

namespace Stanza.Tests;

public class TrackDurationEvidenceTests
{
    [Fact]
    public void 七夕切歌不能带入上一首的286秒时长()
    {
        var evidence = new TrackDurationEvidence();
        Assert.Equal(286284, evidence.Read("上一首", 286284, 900, 1000));
        Assert.Null(evidence.Read("七夕/银临", 286284, 900, 1100));
        Assert.Null(evidence.Read("七夕/银临", 286284, 1099, 1200));
        Assert.Equal(226156, evidence.Read("七夕/银临", 226156, 1150, 1200));
    }

    [Fact]
    public void 同时长换歌也等待新快照且首次接入中段不丢时长()
    {
        var evidence = new TrackDurationEvidence();
        Assert.Equal(200000, evidence.Read("甲", 200000, 1000, 2000));
        Assert.Null(evidence.Read("乙", 200000, 1000, 2100));
        Assert.Equal(200000, evidence.Read("乙", 200000, 2200, 2300));
        evidence.Reset();
        Assert.Equal(180000, evidence.Read("甲", 180000, 2000, 2400));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(1000, 0)]
    [InlineData(1000, -100)]
    public void 无效时长或时间戳不作为匹配证据(int duration, long timestamp)
        => Assert.Null(new TrackDurationEvidence().Read("歌曲", duration, timestamp, 1000));
}
