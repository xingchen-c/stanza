using FluentLyrics.Services.Lyrics;
using Xunit;

namespace FluentLyrics.Tests;

/// <summary>QQ 歌词源匹配/解析纯函数的行为锁定（用历史上出过 bug 的真实场景）</summary>
public class QqMusicMatchingTests
{
    // ---- 歌名相关性 ----

    [Theory]
    [InlineData("伴宅日记", "伴宅日记", true)]                  // 完全一致
    [InlineData("【洛天依】伴宅日记【H6音乐】", "伴宅日记", true)] // 合集/翻唱标题包含原名
    [InlineData("伴宅日记（伴奏）", "伴宅日记", true)]           // 伴奏后缀
    [InlineData("Stairway to Heaven", "stairway to heaven", true)] // 大小写
    [InlineData("狼人杀狂想曲", "伴宅日记", false)]               // 历史 bug：同歌手不同歌混入候选
    [InlineData("理想不大巡演上海站回顾：最搞笑致谢环节！", "理想不大", true)] // 巡演回顾也含歌名
    public void TitlesRelated_判断歌名是否同一首(string lyricsTitle, string expectedTitle, bool expected)
    {
        Assert.Equal(expected, QqMusicLyricsProvider.TitlesRelated(lyricsTitle, expectedTitle));
    }

    // ---- 匹配度排序权重 ----

    [Theory]
    [InlineData("Perfect", 5)]
    [InlineData("PrettyHigh", 4)]
    [InlineData("High", 3)]
    [InlineData("Medium", 2)]
    [InlineData("Low", 1)]
    [InlineData("Unknown", 0)]
    [InlineData(null, 0)]
    [InlineData("未来版本的新枚举名", 0)] // 库升级改名时静默落底，测试锚定现状
    public void MatchTypeRank_权重排序(string? matchType, int expected)
    {
        Assert.Equal(expected, QqMusicLyricsProvider.MatchTypeRank(matchType));
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
