using Xunit;

namespace Stanza.Tests;

/// <summary>公共标题与歌手规则直接锁定内核入口，避免依赖具体词源的转发方法。</summary>
public sealed class LyricsMatchingTests
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
        Assert.Equal(expected, LyricsMatching.StripLeadingBracket(title));
    }

    [Theory]
    [InlineData("回旋", "回旋木马", false)]
    [InlineData("黑夜回旋", "回旋", false)]
    [InlineData("星", "星炬不熄", false)]
    [InlineData("【盗墓笔记】见闻山海 (剧情版)", "见闻山海", true)]
    [InlineData("Song (Live)", "Song - Live Version", true)]
    [InlineData("Song - Live Version", "Song (Live)", true)]
    [InlineData("Song - Other Song", "Song", false)]
    [InlineData("题都城南庄 (古诗诵读)", "题都城南庄", false)]
    [InlineData("题都城南庄(儿童古诗词)", "题都城南庄", false)]
    [InlineData("题都城南庄(崔护·唐)", "题都城南庄", false)]
    [InlineData("题都城南庄（朗诵）", "题都城南庄", false)]
    [InlineData("题都城南庄 (儿童精选古诗词)", "题都城南庄", false)]
    [InlineData("题都城南庄", "题都城南庄（朗诵）", false)]
    [InlineData("题都城南庄（朗诵）", "题都城南庄（朗诵）", true)]
    [InlineData("题都城南庄（Live）", "题都城南庄", true)]
    [InlineData("题都城南庄 (伴奏版)", "题都城南庄", true)]
    [InlineData("Song (Remix)", "Song - Remix", true)]
    [InlineData("Stairway to Heaven", " stairway TO heaven ", true)]
    [InlineData("花", "花", true)]
    [InlineData("", "", false)]
    [InlineData(" ", "Song", false)]
    public void 核心歌名相关性不以子串代替同曲(string candidate, string expected, bool related)
    {
        Assert.Equal(related, LyricsMatching.IsSameSongTitle(candidate, expected));
    }

    // ---- 歌手档识别 ----

    [Fact]
    public void ArtistScore_相等满分()
    {
        AssertClose(1.0, LyricsMatching.ArtistScore(["李凯年"], "李凯年"));
    }

    [Fact]
    public void ArtistScore_多歌手集合相同分隔符不同满分()
    {
        // 候选 join 后 "李凯年,李琳"，期望 "李凯年/李琳"：集合相同 → 1.0
        AssertClose(1.0, LyricsMatching.ArtistScore(["李琳", "李凯年"], "李凯年/李琳"));
    }

    [Fact]
    public void ArtistScore_部分交集()
    {
        // 候选 ["李凯年","张三"] 与期望 "李凯年/李琳" 无包含关系，但共享"李凯年" → 0.6
        AssertClose(0.6, LyricsMatching.ArtistScore(["李凯年", "张三"], "李凯年/李琳"));
    }

    [Fact]
    public void ArtistScore_无交集()
    {
        AssertClose(0.2, LyricsMatching.ArtistScore(["许嵩"], "李凯年"));
    }

    [Fact]
    public void ArtistScore_期望歌手空为缺失()
    {
        Assert.Null(LyricsMatching.ArtistScore(["许嵩"], null));
    }
}
