using FluentLyrics.Services.Lyrics;
using Xunit;

namespace FluentLyrics.Tests;

/// <summary>QQ 歌词源匹配/解析纯函数的行为锁定（用历史上出过 bug 的真实场景）</summary>
public class QqMusicMatchingTests
{
    /// <summary>浮点断言：xUnit 的 precision 重载在 2.9 有歧义，用容差替代</summary>
    private static void AssertClose(double expected, double? actual, double tolerance = 1e-5)
    {
        Assert.True(actual is { } v && Math.Abs(expected - v) <= tolerance,
            $"期望 {expected}，实际 {actual}");
    }

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

    // ---- 综合打分（MATCH-SCORING）----

    [Fact]
    public void TitleScore_完全相等满分()
    {
        AssertClose(1.0, QqMusicLyricsProvider.TitleScore("伴宅日记", "伴宅日记"));
    }

    [Fact]
    public void TitleScore_合集长标题低于满分()
    {
        var score = QqMusicLyricsProvider.TitleScore("【洛天依】伴宅日记【H6音乐】", "伴宅日记");
        Assert.True(score < 1.0 && score > 0.6);
    }

    [Fact]
    public void DurationScore_平滑衰减锁定Tau()
    {
        AssertClose(1.0, QqMusicLyricsProvider.DurationScore(1000, 1000));
        AssertClose(0.5, QqMusicLyricsProvider.DurationScore(9000, 1000), 0.01); // Δ=8s → 0.5
        Assert.True(QqMusicLyricsProvider.DurationScore(31000, 1000)!.Value < 0.1); // Δ=30s → ≈0.07
    }

    [Fact]
    public void DurationScore_任一时长缺失为缺失()
    {
        Assert.Null(QqMusicLyricsProvider.DurationScore(null, 1000));
        Assert.Null(QqMusicLyricsProvider.DurationScore(1000, null));
    }

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

    [Fact]
    public void 全维匹配高分()
    {
        var score = QqMusicLyricsProvider.ComputeMatchScore(
            "美人蕉", ["李凯年", "李琳"], "专辑", 234000, "PrettyHigh",
            "美人蕉", "李凯年/李琳", 237000);
        Assert.True(score > 0.9);
    }

    [Fact]
    public void 时长严重错报仍保持中高分_不再一票否决()
    {
        // 历史场景：伴宅日记 SMTC 错报 210s，实际 263s，差 53s
        var score = QqMusicLyricsProvider.ComputeMatchScore(
            "伴宅日记", ["千月兔"], null, 263000, "High",
            "伴宅日记", "千月兔", 210000);
        Assert.True(score > 0.5);
    }

    [Fact]
    public void 同名两候选时长近者优先()
    {
        double Score(int duration) => QqMusicLyricsProvider.ComputeMatchScore(
            "伴宅日记", ["千月兔"], null, duration, "Medium",
            "伴宅日记", "千月兔", 263000);
        Assert.True(Score(263000) > Score(210000));
    }

    [Fact]
    public void 时长缺失归一化重分配_不比时长严重错误更差()
    {
        double WithoutDuration() => QqMusicLyricsProvider.ComputeMatchScore(
            "伴宅日记", ["千月兔"], null, null, "High",
            "伴宅日记", "千月兔", null);
        double WithBadDuration() => QqMusicLyricsProvider.ComputeMatchScore(
            "伴宅日记", ["千月兔"], null, 1000, "High",
            "伴宅日记", "千月兔", 263000); // 时长分≈0
        Assert.True(WithoutDuration() > WithBadDuration());
    }

    // ---- 排序主键：歌手精确度优先（同名不同歌手不串歌）----

    [Fact]
    public void ArtistRankKey_精确歌手高于部分交集高于无交集()
    {
        var exact = QqMusicLyricsProvider.ArtistRankKey(["ZWLJ", "国风引力"], "ZWLJ/国风引力");
        var partial = QqMusicLyricsProvider.ArtistRankKey(["ZWLJ", "张三"], "ZWLJ/国风引力");
        var none = QqMusicLyricsProvider.ArtistRankKey(["童译"], "ZWLJ/国风引力");
        Assert.True(exact > partial && partial > none);
    }

    [Fact]
    public void ArtistRankKey_期望歌手缺失时为最低档()
    {
        // SMTC 给不出歌手时，主键退化为 0，排序退回综合分（时长+MatchType），不误排
        AssertClose(0, QqMusicLyricsProvider.ArtistRankKey(["许嵩"], null));
    }

    [Fact]
    public void 同名不同歌手_时长凑巧接近也不反超精确歌手_豆蔻案例()
    {
        // 真实场景：SMTC 报「豆蔻 / ZWLJ/国风引力 / 235440ms」，QQ 返回两个同名候选——
        // 正确歌手 ZWLJ/国风引力（时长差 28s）与错歌手 童译（时长差仅 1.4s）。
        double scoreCorrect = QqMusicLyricsProvider.ComputeMatchScore(
            "豆蔻", ["ZWLJ", "国风引力"], "豆蔻", 207000, "Medium",
            "豆蔻", "ZWLJ/国风引力", 235440);
        double scoreWrong = QqMusicLyricsProvider.ComputeMatchScore(
            "豆蔻", ["童译"], "好听的BGM背景纯音乐推荐大全53", 234000, "Medium",
            "豆蔻", "ZWLJ/国风引力", 235440);

        // 旧分排序会选错（证明本修复靠的是「歌手主键」，而不是调权重掩盖）
        Assert.True(scoreWrong > scoreCorrect);

        // 新排序：主键（歌手精确度）决定顺序，正确候选恒排在错候选前
        double rankCorrect = QqMusicLyricsProvider.ArtistRankKey(["ZWLJ", "国风引力"], "ZWLJ/国风引力");
        double rankWrong = QqMusicLyricsProvider.ArtistRankKey(["童译"], "ZWLJ/国风引力");
        Assert.True(rankCorrect > rankWrong);
    }

    [Fact]
    public void 同名多版本_同歌手档内仍靠时长选版本()
    {
        // 同歌手（主键同为 1.0）时，时长近者综合分更高 → 选对版本（DJ/Live/伴奏）
        double Score(int duration) => QqMusicLyricsProvider.ComputeMatchScore(
            "长生诀", ["西瓜JUN"], null, duration, "Medium",
            "长生诀", "西瓜JUN", 191000);
        Assert.True(Score(191000) > Score(23000)); // 原曲 191s > 23s 片段版
    }
}
