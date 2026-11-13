using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Stanza.Models;
using Stanza.Services.Lyrics;
using Xunit;

namespace Stanza.Tests;

/// <summary>离线验证生产候选准入与排序，不调用搜索、取词或应用日志。</summary>
public class QqCandidateSelectionTests
{
    [Fact]
    public void MatchType_真实库枚举契约和评分严格单调()
    {
        (CompareHelper.MatchType Type, int Value)[] levels =
        [
            (CompareHelper.MatchType.VeryLow, 10),
            (CompareHelper.MatchType.Low, 30),
            (CompareHelper.MatchType.Medium, 70),
            (CompareHelper.MatchType.PrettyHigh, 90),
            (CompareHelper.MatchType.High, 95),
            (CompareHelper.MatchType.VeryHigh, 99),
            (CompareHelper.MatchType.Perfect, 100)
        ];
        Assert.Equal(-1, (int)CompareHelper.MatchType.NoMatch);
        Assert.Equal(0, QqMusicLyricsProvider.MatchTypeScore(CompareHelper.MatchType.NoMatch));
        var previous = 0.0;
        foreach (var (type, value) in levels)
        {
            Assert.Equal(value, (int)type);
            Assert.Equal(value, QqMusicLyricsProvider.MatchTypeRank(type));
            var score = QqMusicLyricsProvider.MatchTypeScore(type);
            Assert.InRange(score, 0, 1);
            Assert.True(score > previous, $"{type} 应高于上一等级");
            previous = score;
        }
        Assert.Equal(1, previous);
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
        Assert.Equal(related, QqMusicLyricsProvider.IsSameSongTitle(candidate, expected));
    }

    [Fact]
    public void 自动准入_保留旧语义并接纳清洗后的版本()
    {
        Assert.True(QqMusicLyricsProvider.TitlesRelated("回旋木马", "回旋"));
        Assert.False(QqMusicLyricsProvider.TitlesRelated("Song - Live Version", "Song (Live)"));
        Assert.True(QqMusicLyricsProvider.IsSameSongForAutoMatch("Song - Live Version", "Song (Live)"));
        var selection = QqMusicLyricsProvider.PrepareAutoCandidates(
            [Result("live", "Song - Live Version", CompareHelper.MatchType.VeryHigh)],
            new LyricsQuery("Song (Live)", "歌手", 200000));
        Assert.Single(selection.Ranked);
        Assert.Equal("live", selection.Ranked[0].Candidate.Id);
        Assert.False(selection.UsedFallback);
    }

    [Fact]
    public void VeryHigh版本在生产排序的前四个尝试范围内()
    {
        QQMusicSearchResult[] results =
        [
            Result("medium1", "Song (Live)", CompareHelper.MatchType.Medium),
            Result("medium2", "Song (Live)", CompareHelper.MatchType.Medium),
            Result("pretty", "Song (Live)", CompareHelper.MatchType.PrettyHigh),
            Result("high", "Song (Live)", CompareHelper.MatchType.High),
            Result("very", "Song - Live Version", CompareHelper.MatchType.VeryHigh),
            Result("perfect", "Song (Live)", CompareHelper.MatchType.Perfect)
        ];
        var selection = QqMusicLyricsProvider.PrepareAutoCandidates(results,
            new LyricsQuery("Song (Live)", "歌手", 200000));
        Assert.Equal(new[] { "perfect", "very", "high", "pretty", "medium1", "medium2" },
            selection.Ranked.Select(x => x.Candidate.Id));
        Assert.Contains(selection.Ranked.Take(4), x => x.Candidate.Id == "very");
        // MatchType 只证明元数据匹配；这里不声称该候选一定能取到歌词。
    }

    [Fact]
    public void 同名候选精确歌手优先且不会被错歌手时长反超()
    {
        var selection = QqMusicLyricsProvider.PrepareAutoCandidates(
        [
            Result("wrong", "豆蔻", CompareHelper.MatchType.Perfect, ["童译"], 235440),
            Result("partial", "豆蔻", CompareHelper.MatchType.Perfect, ["ZWLJ", "张三"], 235440),
            Result("exact", "豆蔻", CompareHelper.MatchType.Medium, ["ZWLJ", "国风引力"], 207000)
        ], new LyricsQuery("豆蔻", "ZWLJ/国风引力", 235440));
        Assert.False(selection.UsedFallback);
        Assert.Equal(new[] { "exact", "partial" }, selection.Ranked.Select(x => x.Candidate.Id));
    }

    [Fact]
    public void 词库缺歌手时允许歌名降级而不是整源放弃()
    {
        var selection = QqMusicLyricsProvider.PrepareAutoCandidates(
            [Result("missing", "Song - Live Version", CompareHelper.MatchType.VeryHigh, [])],
            new LyricsQuery("Song (Live)", "歌手", 200000));
        Assert.True(selection.UsedFallback);
        Assert.Single(selection.Ranked);
    }

    [Fact]
    public void 期望歌手缺失时使用综合分且稳定保留同分原顺序()
    {
        var selection = QqMusicLyricsProvider.PrepareAutoCandidates(
        [
            Result("medium", "Song", CompareHelper.MatchType.Medium),
            Result("first", "Song", CompareHelper.MatchType.VeryHigh),
            Result("second", "Song", CompareHelper.MatchType.VeryHigh)
        ], new LyricsQuery("Song", "", 200000));
        Assert.False(selection.UsedFallback);
        Assert.Equal(new[] { "first", "second", "medium" }, selection.Ranked.Select(x => x.Candidate.Id));
    }

    [Fact]
    public async Task 手动跨源列表保留全部且同曲优先于高分同歌手噪声()
    {
        var query = new LyricsQuery("回旋", "歌手", 200000);
        var qq = new QqMusicLyricsProvider().PrepareManualCandidates(
        [
            Result("noise", "回旋木马", CompareHelper.MatchType.Perfect),
            Result("black", "黑夜回旋", CompareHelper.MatchType.Perfect),
            Result("related", "回旋 (Live)", CompareHelper.MatchType.Medium, ["童译"])
        ], query);
        Assert.Equal(3, qq.Count);
        var external = new LyricCandidate("Other", "其它来源", "external", "回旋", "歌手", null, null, null)
            { MatchScore = 0.5, ArtistMatched = true, TitleMatched = false };
        var candidates = await new LyricsProviderChain(
            new CandidateProvider("QQ", qq), new CandidateProvider("Other", [external]))
            .GetCandidatesAsync(query);
        Assert.Equal(4, candidates.Count);
        Assert.Equal(new[] { "external", "related", "noise", "black" }, candidates.Select(x => x.Id));
        Assert.All(candidates.Take(2), x => Assert.True(x.TitleMatched));
        Assert.All(candidates.Skip(2), x => Assert.False(x.TitleMatched));
        Assert.False(external.TitleMatched); // 链只标注副本，不污染提供器持有的对象。
    }

    [Fact]
    public async Task 选词列表_过滤截图中的异歌及朗诵作品_勾选只放宽歌手()
    {
        var query = new LyricsQuery("题都城南庄", "岑今山（停用）", 200000);
        var related = new LyricCandidate("QQ", "QQ 音乐", "related", "题都城南庄（Live）", "岑今山", null, null, null)
            { ArtistMatched = true };
        var otherArtist = related with { Id = "other-artist", Artist = "其他歌手", ArtistMatched = false };
        var source = new CandidateProvider("QQ",
        [
            related,
            otherArtist,
            related with { Id = "island", Title = "屿" },
            related with { Id = "reading", Title = "题都城南庄（朗诵）", ArtistMatched = false },
            related with { Id = "poem", Title = "题都城南庄 (古诗诵读)", ArtistMatched = false },
            related with { Id = "children", Title = "题都城南庄(儿童古诗词)", ArtistMatched = false },
            related with { Id = "poet", Title = "题都城南庄(崔护·唐)", ArtistMatched = false }
        ]);

        var candidates = await new LyricsProviderChain(source).GetCandidatesAsync(query);

        Assert.Equal(["related"], LyricsPickerWindow.FilterCandidates(candidates, false).Select(candidate => candidate.Id));
        Assert.Equal(["related", "other-artist"],
            LyricsPickerWindow.FilterCandidates(candidates, true).Select(candidate => candidate.Id));
    }

    [Fact]
    public void 专辑输入参与自动和手动评分_同专辑不低于缺失且优于冲突()
    {
        var results = new[] { Result("song", "Song", CompareHelper.MatchType.VeryHigh, album: "专辑") };
        var query = new LyricsQuery("Song", "歌手", 200000, "专辑");
        var missing = query with { Album = null };
        var conflict = query with { Album = "乙唱片" };
        var sameScore = QqMusicLyricsProvider.PrepareAutoCandidates(results, query).Ranked[0].Score;
        var missingScore = QqMusicLyricsProvider.PrepareAutoCandidates(results, missing).Ranked[0].Score;
        var conflictScore = QqMusicLyricsProvider.PrepareAutoCandidates(results, conflict).Ranked[0].Score;
        Assert.True(sameScore >= missingScore);
        Assert.True(conflictScore < sameScore);
        var provider = new QqMusicLyricsProvider();
        Assert.Equal(sameScore, provider.PrepareManualCandidates(results, query)[0].MatchScore);
        Assert.Equal(missingScore, provider.PrepareManualCandidates(results, missing)[0].MatchScore);
        Assert.Equal(conflictScore, provider.PrepareManualCandidates(results, conflict)[0].MatchScore);
    }

    private static QQMusicSearchResult Result(string id, string title, CompareHelper.MatchType matchType,
        string[]? artists = null, int durationMs = 200000, string album = "专辑")
        => new(title, artists ?? ["歌手"], album, [], durationMs, id, id) { MatchType = matchType };

    private sealed class CandidateProvider(string key, IReadOnlyList<LyricCandidate> candidates) : ILyricsProvider
    {
        public string Key => key;
        public string Name => key;
        public Task<LyricsResult> LoadAsync(LyricsQuery query)
            => Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));
        public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
            => Task.FromResult(candidates);
    }
}
