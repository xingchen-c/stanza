using Stanza.Services.Lyrics;
using Xunit;

namespace Stanza.Tests;

/// <summary>QQ 歌词源匹配/解析纯函数的行为锁定（用历史上出过 bug 的真实场景）</summary>
public class QqMusicMatchingTests
{
    /// <summary>浮点断言：xUnit 的 precision 重载在 2.9 有歧义，用容差替代</summary>
    private static void AssertClose(double expected, double? actual, double tolerance = 1e-5)
    {
        Assert.True(actual is { } v && Math.Abs(expected - v) <= tolerance,
            $"期望 {expected}，实际 {actual}");
    }

    // ---- 标题前缀清洗 ----

    [Theory]
    [InlineData("【盗墓笔记】见闻山海", "见闻山海")]     // 全角方括号前缀
    [InlineData("[盗墓笔记]见闻山海", "见闻山海")]       // 半角方括号前缀
    [InlineData("【盗墓笔记】见闻山海 (剧情版)", "见闻山海 (剧情版)")] // 只去前缀、保留后缀
    [InlineData("见闻山海", null)]                       // 无前缀
    [InlineData("【】", null)]                            // 前缀为空
    [InlineData("（盗墓笔记）见闻山海", null)]           // 圆括号开头不算方括号前缀
    public void StripLeadingBracket_去掉开头方括号前缀(string title, string? expected)
    {
        Assert.Equal(expected, QqMusicLyricsProvider.StripLeadingBracket(title));
    }

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

    // ---- 歌手档识别 ----

    [Fact]
    public void ArtistScore_相等满分()
    {
        AssertClose(1.0, QqMusicLyricsProvider.ArtistScore(["李凯年"], "李凯年"));
    }

    [Fact]
    public void ArtistScore_多歌手集合相同分隔符不同满分()
    {
        // 候选 join 后 "李凯年,李琳"，期望 "李凯年/李琳"：集合相同 → 1.0
        AssertClose(1.0, QqMusicLyricsProvider.ArtistScore(["李琳", "李凯年"], "李凯年/李琳"));
    }

    [Fact]
    public void ArtistScore_部分交集()
    {
        // 候选 ["李凯年","张三"] 与期望 "李凯年/李琳" 无包含关系，但共享"李凯年" → 0.6
        AssertClose(0.6, QqMusicLyricsProvider.ArtistScore(["李凯年", "张三"], "李凯年/李琳"));
    }

    [Fact]
    public void ArtistScore_无交集()
    {
        AssertClose(0.2, QqMusicLyricsProvider.ArtistScore(["许嵩"], "李凯年"));
    }

    [Fact]
    public void ArtistScore_期望歌手空为缺失()
    {
        Assert.Null(QqMusicLyricsProvider.ArtistScore(["许嵩"], null));
    }

}
