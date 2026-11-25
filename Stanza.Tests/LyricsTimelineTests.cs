using Stanza.Core.Models;
using Stanza.Core.Lyrics;
using Xunit;

namespace Stanza.Tests;

/// <summary>按播放位置取当前行（二分查找）的行为锁定</summary>
public class LyricsTimelineTests
{
    private static readonly List<LyricLine> Lines =
    [
        new(1000, "第一句"),
        new(3000, "第二句"),
        new(5000, "第三句")
    ];

    [Fact]
    public void 空歌词返回负一()
    {
        Assert.Equal(-1, LyricsTimeline.GetLineIndexAt([], 0));
    }

    [Fact]
    public void 负位置返回负一()
    {
        Assert.Equal(-1, LyricsTimeline.GetLineIndexAt(Lines, -1));
    }

    [Theory]
    [InlineData(0, 0)]     // 还没到第一句：提前显示第一行
    [InlineData(999, 0)]
    [InlineData(1000, 0)]  // 第一句开始
    [InlineData(2999, 0)]
    [InlineData(3000, 1)]  // 第二句开始
    [InlineData(4999, 1)]
    [InlineData(5000, 2)]  // 第三句开始
    [InlineData(99999, 2)] // 超出最后一句：停在最后一行
    public void 二分定位当前行(int positionMs, int expected)
    {
        Assert.Equal(expected, LyricsTimeline.GetLineIndexAt(Lines, positionMs));
    }
}
