using Stanza.Core.Lyrics;
using Xunit;

namespace Stanza.Tests;

/// <summary>QQ 歌词源召回/解析纯函数的行为锁定（用历史上出过 bug 的真实场景）</summary>
public class QqMusicMatchingTests
{
    // ---- 群星歌手截断 ----

    [Theory]
    [InlineData(null, 2, null)]
    [InlineData("", 2, "")]
    [InlineData("司夏", 2, "司夏")]                          // 单歌手原样
    [InlineData("蔡明希-不才/云之泣/银临/Aki阿杰", 2, "蔡明希-不才/云之泣")] // 多歌手截到前两位
    [InlineData("李常超（Lao乾妈）/音频怪物/张承煜（小魂）", 2, "李常超（Lao乾妈）/音频怪物")]
    public void ShortenArtists_群星歌手截到前N位(string? artist, int maxCount, string? expected)
    {
        Assert.Equal(expected, QqMusicLyricsProvider.ShortenArtists(artist, maxCount));
    }

    // ---- 时间戳判定 ----

    [Theory]
    [InlineData("[00:12.34]歌词内容", true)]
    [InlineData("[00:01.00]第一句\n[00:02.00]第二句", true)]
    [InlineData("没有时间戳的纯文本歌词", false)]
    [InlineData("[ti:歌名]", false)]      // 元数据标签不是 [mm:ss]
    [InlineData("[歌词内容缺右括号", false)]
    public void LooksLikeTimedLyrics_区分时间轴与占位(string raw, bool expected)
    {
        Assert.Equal(expected, QqMusicLyricsProvider.LooksLikeTimedLyrics(raw));
    }

    // ---- 翻译对齐 ----

    [Fact]
    public void BuildTranslationMap_解析LRC翻译()
    {
        var map = QqMusicLyricsProvider.BuildTranslationMap("[00:01.00]你好\n[00:02.00]世界");
        Assert.True(map.TryGetValue(1000, out var first) && first == "你好");
        Assert.True(map.TryGetValue(2000, out var second) && second == "世界");
    }

    [Fact]
    public void BuildTranslationMap_过滤占位符()
    {
        // QQ 翻译里 "//" 表示该行无翻译，不能挂到歌词上
        var map = QqMusicLyricsProvider.BuildTranslationMap("[00:01.00]你好\n[00:02.00]//");
        Assert.True(map.TryGetValue(1000, out _));
        Assert.False(map.ContainsKey(2000));
    }

    [Fact]
    public void MatchByTime_精确命中优先()
    {
        var map = new Dictionary<int, string> { [1000] = "你好" };
        Assert.Equal("你好", QqMusicLyricsProvider.MatchByTime(1000, map));
    }

    [Fact]
    public void MatchByTime_容差内取最近()
    {
        var map = new Dictionary<int, string> { [1000] = "你好" };
        Assert.Equal("你好", QqMusicLyricsProvider.MatchByTime(1100, map)); // ±300ms 内
        Assert.Null(QqMusicLyricsProvider.MatchByTime(2000, map));          // 超出容差
    }


}
