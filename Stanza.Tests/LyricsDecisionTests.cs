using Stanza.Core.Models;

using Stanza.Core.Lyrics;
using Lyricify.Lyrics.Searchers.Helpers;
using Xunit;

namespace Stanza.Tests;

public class LyricsDecisionTests
{
    private static readonly LyricsQuery Query = new("唤归", "歌手", 226156, "唤归", "QQ");

    [Fact]
    public async Task 跨源同曲优先播放平台且自动与手动共用顺序()
    {
        var qq = new Provider("QQ", Candidate("QQ"), Result("QQ"));
        var ne = new Provider("NetEase", Candidate("NetEase"), Result("网易"));
        var chain = new LyricsProviderChain(ne, qq);
        var candidates = await chain.GetCandidatesAsync(Query);
        Assert.Equal("QQ", candidates[0].SourceKey);
        Assert.Equal(candidates[0].MatchScore, candidates[1].MatchScore);
        var result = await chain.LoadAsync(Query);
        Assert.Equal("QQ", result.Lines.Single().Text);
        Assert.Equal("QQ", result.Decision!.Candidate.SourceKey);
    }

    [Fact]
    public async Task 同平台取词失败继续其它源且不调用旧自动入口()
    {
        var chain = new LyricsProviderChain(
            new Provider("QQ", Candidate("QQ"), new(LyricsLoadResult.NotFound, [])),
            new Provider("NetEase", Candidate("NetEase"), Result("回退")));
        Assert.Equal("回退", (await chain.LoadAsync(Query)).Lines.Single().Text);
    }

    [Fact]
    public async Task 同平台多个重复版本失败后仍能尝试其它来源()
    {
        var qq = Candidate("QQ");
        var chain = new LyricsProviderChain(
            new Provider("QQ", qq, new(LyricsLoadResult.NotFound, []),
                qq with { Id = "2" }, qq with { Id = "3" }, qq with { Id = "4" }),
            new Provider("NetEase", Candidate("NetEase"), Result("其它来源")));
        Assert.Equal("其它来源", (await chain.LoadAsync(Query)).Lines.Single().Text);
    }

    [Theory]
    [InlineData("唤归 (Live)")]
    [InlineData("唤归 (伴奏版)")]
    [InlineData("唤归 (Remix)")]
    public async Task 同源偏好不放行明显不同版本(string title)
    {
        var chain = new LyricsProviderChain(
            new Provider("QQ", Candidate("QQ") with { Title = title }, Result("错版")),
            new Provider("NetEase", Candidate("NetEase"), Result("原版")));
        Assert.Equal("原版", (await chain.LoadAsync(Query)).Lines.Single().Text);
    }

    [Fact]
    public async Task 其它来源明显更匹配时胜出()
    {
        var chain = new LyricsProviderChain(
            new Provider("QQ", Candidate("QQ") with { DurationMs = 244000, Album = "其它专辑" }, Result("错版")),
            new Provider("NetEase", Candidate("NetEase"), Result("正确")));
        Assert.Equal("正确", (await chain.LoadAsync(Query)).Lines.Single().Text);
    }

    [Fact]
    public async Task 无时长且不同录音接近时不自动猜选()
    {
        var query = Query with { DurationMs = null, Album = null };
        var chain = new LyricsProviderChain(
            new Provider("QQ", Candidate("QQ") with { Album = "甲录音" }, Result("甲")),
            new Provider("NetEase", Candidate("NetEase") with { Album = "乙录音", DurationMs = 244000 }, Result("乙")));
        Assert.Equal(LyricsLoadResult.Ambiguous, (await chain.LoadAsync(query)).Status);
    }

    [Fact]
    public async Task 中立播放器在同等匹配中比较逐字和翻译质量()
    {
        var rich = new LyricsResult(LyricsLoadResult.Loaded, [new(0, "完整", 1000, "译文", [new("完", 0, 1000)])]);
        var chain = new LyricsProviderChain(
            new Provider("QQ", Candidate("QQ"), Result("逐行")),
            new Provider("NetEase", Candidate("NetEase"), rich));
        Assert.Equal("完整", (await chain.LoadAsync(Query with { PreferredSourceKey = null })).Lines.Single().Text);
    }

    [Fact]
    public void 歌曲分不信任提供器已加权分数且来源偏好不改变分数()
    {
        var a = LyricsCandidateRanker.Score(Candidate("QQ") with { MatchScore = 0.01, MatchType = "Low" }, Query);
        var b = LyricsCandidateRanker.Score(Candidate("NetEase") with { MatchScore = 1, MatchType = "Perfect" }, Query);
        Assert.Equal(a.MatchType, b.MatchType);
        Assert.Equal(a.MatchScore, b.MatchScore);
        Assert.True(a.PreferredSource);
        Assert.False(b.PreferredSource);
    }

    [Theory]
    [InlineData("QQ", "QQMusic.exe", "QQ")]
    [InlineData("NetEase", "cloudmusic.exe", "NetEase")]
    [InlineData("Generic", "Tencent.QQMusic", "QQ")]
    [InlineData("Generic", "tencent.qqmusic", "QQ")]
    [InlineData("Generic", "chrome.exe", null)]
    public void 播放来源偏好识别已知平台而不改变播放策略(string policy, string aumid, string? expected)
        => Assert.Equal(expected, PlaybackWorkflow.PreferredLyricsSource(policy, aumid));

    [Fact]
    public async Task 唤归截图的双歌手同曲同秒数优先QQ()
    {
        var query = new LyricsQuery("唤归", "王子健/十八薰", 226000, "唤归", "QQ");
        var candidate = Candidate("NetEase") with { Title = query.Title, Artist = "王子健, 十八薰" };
        var chain = new LyricsProviderChain(
            new Provider("NetEase", candidate, Result("网易")),
            new Provider("QQ", candidate with { SourceKey = "QQ" }, Result("QQ")));
        Assert.Equal("QQ", (await chain.LoadAsync(query)).Decision!.Candidate.SourceKey);
    }

    [Fact]
    public async Task 七夕稳定时长后跨源选择226秒而非244秒版本()
    {
        var query = new LyricsQuery("七夕", "银临", 226156, null, "QQ");
        var candidate = Candidate("QQ") with { Title = query.Title, Artist = query.Artist, Album = null, DurationMs = 244000 };
        var chain = new LyricsProviderChain(
            new Provider("QQ", candidate, Result("244秒版")),
            new Provider("NetEase", candidate with { SourceKey = "NetEase", DurationMs = 226000 }, Result("226秒版")));
        Assert.Equal("226秒版", (await chain.LoadAsync(query)).Lines.Single().Text);
    }

    [Fact]
    public void 豆蔻精确歌手优先但时长冲突不强行自动选择()
    {
        var query = new LyricsQuery("豆蔻", "ZWLJ/国风引力", 235440);
        var candidate = Candidate("QQ") with { Title = query.Title, Artist = "童译", DurationMs = 235440, Album = null };
        var ranked = LyricsCandidateRanker.Rank([
            candidate with { Id = "wrong" },
            candidate with { Id = "partial", Artist = "ZWLJ, 张三" },
            candidate with { Id = "exact", Artist = "ZWLJ, 国风引力", DurationMs = 207000 }
        ], query);
        Assert.Equal(["exact", "partial", "wrong"], ranked.Select(c => c.Id));
        Assert.Equal(LyricsLoadResult.Ambiguous, LyricsCandidateRanker.Decide(ranked, query).Status);
    }

    [Fact]
    public void 缺少匹配歌手仍保留同名候选供手动纠错()
    {
        var ranked = LyricsCandidateRanker.Rank([Candidate("QQ") with { Artist = "" }], Query);
        Assert.Single(ranked);
        Assert.True(ranked[0].TitleMatched);
        Assert.Equal(LyricsLoadResult.Ambiguous, LyricsCandidateRanker.Decide(ranked, Query).Status);
    }

    [Theory]
    [InlineData(null, "QQ")]
    [InlineData(205263, "QQ")]
    [InlineData(null, "NetEase")]
    [InlineData(205263, "NetEase")]
    public async Task 心似悬春完整歌手候选不被部分歌手的同档高分阻止(
        int? durationMs, string preferredSource)
    {
        var query = new LyricsQuery("心似悬春", "喵酱油/小坠/折光组", durationMs, "心似悬春", preferredSource);
        var qq = new LyricCandidate("QQ", "QQ 音乐", "430662886", query.Title,
            "喵酱油, 小坠, 折光组", query.Album, 205000, null);
        var ne = new LyricCandidate("NetEase", "网易云音乐", "2079399745", query.Title,
            "小坠, 喵☆酱, 折光组", query.Album, 205263, null);
        var chain = new LyricsProviderChain(
            new Provider("NetEase", ne, Result("部分歌手")),
            new Provider("QQ", qq, Result("完整歌手")));

        var ranked = await chain.GetCandidatesAsync(query);
        Assert.Equal(qq.Id, ranked[0].Id);
        Assert.All(ranked, candidate =>
        {
            Assert.True(candidate.ArtistMatched);
            Assert.True(candidate.MatchScore >= 0.99);
        });
        Assert.False(LyricsCandidateRanker.SameRecording(ranked[0], ranked[1]));

        var result = await chain.LoadAsync(query);
        Assert.Equal(LyricsLoadResult.Loaded, result.Status);
        Assert.Equal(qq.Id, result.Decision!.Candidate.Id);
        Assert.Equal("完整歌手", Assert.Single(result.Lines).Text);
    }

    [Theory]
    [InlineData("喵酱油, 小坠, 折光组", "喵酱油, 小坠, 折光组")]
    [InlineData("小坠, 喵☆酱, 折光组", "小坠, 其他歌手, 折光组")]
    public async Task 同一歌手档的高分候选录音冲突仍保留歧义(string qqArtist, string neArtist)
    {
        var query = new LyricsQuery("心似悬春", "喵酱油/小坠/折光组", null, "心似悬春", "QQ");
        var candidate = new LyricCandidate("QQ", "QQ 音乐", "qq", query.Title,
            qqArtist, query.Album, 205000, null);
        var chain = new LyricsProviderChain(
            new Provider("QQ", candidate, Result("205 秒录音")),
            new Provider("NetEase", candidate with
            {
                SourceKey = "NetEase", Id = "ne", Artist = neArtist, DurationMs = 220000
            }, Result("220 秒录音")));

        var ranked = await chain.GetCandidatesAsync(query);
        Assert.All(ranked, c => Assert.True(c.MatchScore >= 0.99));
        Assert.Equal(LyricsLoadResult.Ambiguous, (await chain.LoadAsync(query)).Status);
    }

    [Fact]
    public async Task 完整歌手候选取词失败不把部分歌手候选当作同录音回退()
    {
        var query = new LyricsQuery("心似悬春", "喵酱油/小坠/折光组", 205263, "心似悬春", "QQ");
        var candidate = new LyricCandidate("QQ", "QQ 音乐", "430662886", query.Title,
            "喵酱油, 小坠, 折光组", query.Album, 205000, null);
        var chain = new LyricsProviderChain(
            new Provider("QQ", candidate, new(LyricsLoadResult.NotFound, [])),
            new Provider("NetEase", candidate with
            {
                SourceKey = "NetEase", Id = "2079399745", Artist = "小坠, 喵☆酱, 折光组", DurationMs = 205263
            }, Result("部分歌手")));

        Assert.Equal(LyricsLoadResult.NotFound, (await chain.LoadAsync(query)).Status);
    }

    [Theory]
    [InlineData("Mili (ミリー)", "Mili")]
    [InlineData("Mili（ミリー）", "Mili")]
    [InlineData("Mili", "Mili (ミリー)")]
    public async Task Rendezvous歌手括号别名不阻止跨源自动采用(string artist, string candidateArtist)
    {
        var query = new LyricsQuery("Rendezvous", artist, 208533, "Rendezvous", "QQ");
        var candidate = new LyricCandidate("NetEase", "网易云音乐", "3442131349",
            query.Title, candidateArtist, query.Album, 208533, null);
        var chain = new LyricsProviderChain(new Provider("NetEase", candidate, Result("歌词")));

        var ranked = await chain.GetCandidatesAsync(query);
        var equivalent = LyricsCandidateRanker.Score(candidate with { Artist = "Mili" }, query with { Artist = "Mili" });
        Assert.Equal(equivalent.MatchScore, Assert.Single(ranked).MatchScore);
        var result = await chain.LoadAsync(query);
        Assert.Equal(LyricsLoadResult.Loaded, result.Status);
        Assert.Equal("歌词", Assert.Single(result.Lines).Text);
        Assert.Equal(candidateArtist, result.Decision!.Candidate.Artist);
        Assert.False(result.Decision.Candidate.PreferredSource);
    }

    [Fact]
    public void 只有一条候选仍不能放行时长冲突()
    {
        var candidate = Candidate("NetEase") with { DurationMs = 244000, Album = "其它专辑" };
        var ranked = LyricsCandidateRanker.Rank([candidate], Query);
        Assert.Single(ranked);
        Assert.Equal(LyricsLoadResult.Ambiguous, LyricsCandidateRanker.Decide(ranked, Query).Status);
    }

    [Theory]
    [InlineData("Rendezvous", "其他歌手", LyricsLoadResult.Ambiguous)]
    [InlineData("Rendezvous (Live)", "Mili", LyricsLoadResult.NotFound)]
    public async Task 歌手别名归一后仍不放行异歌手或不同版本(
        string title, string artist, LyricsLoadResult expected)
    {
        var query = new LyricsQuery("Rendezvous", "Mili (ミリー)", 208533, "Rendezvous", "QQ");
        var candidate = new LyricCandidate("NetEase", "网易云音乐", "1", title, artist, query.Album, 208533, null);
        var chain = new LyricsProviderChain(new Provider("NetEase", candidate, Result("错版")));
        Assert.Equal(expected, (await chain.LoadAsync(query)).Status);
    }

    [Fact]
    public async Task 同平台标记纯音乐后仍可采用其它源的可用歌词()
    {
        var chain = new LyricsProviderChain(
            new Provider("QQ", Candidate("QQ"), new(LyricsLoadResult.Instrumental, [])),
            new Provider("NetEase", Candidate("NetEase"), Result("歌词")));
        Assert.Equal("歌词", (await chain.LoadAsync(Query)).Lines.Single().Text);
    }

    [Fact]
    public void 库枚举分数契约保持排序及置信档含义()
    {
        Assert.Equal([-1, 10, 30, 70, 90, 95, 99, 100], new[] {
            CompareHelper.MatchType.NoMatch, CompareHelper.MatchType.VeryLow, CompareHelper.MatchType.Low,
            CompareHelper.MatchType.Medium, CompareHelper.MatchType.PrettyHigh, CompareHelper.MatchType.High,
            CompareHelper.MatchType.VeryHigh, CompareHelper.MatchType.Perfect }.Select(x => (int)x));
    }

    [Fact]
    public void 自动决策缓存按时长专辑及播放平台区分且不读取旧普通key()
    {
        var original = LyricsLoader.AutomaticCacheKey(Query);
        Assert.NotEqual(Query.Key, original);
        Assert.NotEqual(original, LyricsLoader.AutomaticCacheKey(Query with { DurationMs = null }));
        Assert.NotEqual(original, LyricsLoader.AutomaticCacheKey(Query with { Album = "另一张专辑" }));
        Assert.NotEqual(original, LyricsLoader.AutomaticCacheKey(Query with { PreferredSourceKey = "NetEase" }));
        Assert.Equal(original, LyricsLoader.AutomaticCacheKey(Query with { }));
    }

    internal static LyricCandidate Candidate(string source)
        => new(source, source, "1", Query.Title, Query.Artist, Query.Album, 226000, null);
    private static LyricsResult Result(string text) => new(LyricsLoadResult.Loaded, [new(0, text)]);

    private sealed class Provider(string key, LyricCandidate candidate, LyricsResult result,
        params LyricCandidate[] additional) : ILyricsProvider
    {
        public string Key => key;
        public string Name => key;
        public Task<LyricsResult> LoadAsync(LyricsQuery query)
            => throw new InvalidOperationException("自动链必须先统一搜索和决策");
        public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
            => Task.FromResult<IReadOnlyList<LyricCandidate>>([candidate, .. additional]);
        public Task<LyricsResult> LoadCandidateAsync(LyricCandidate selected) => Task.FromResult(result);
    }
}
