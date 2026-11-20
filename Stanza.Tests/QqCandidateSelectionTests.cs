using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Stanza.Models;
using Stanza.Services.Lyrics;
using Xunit;

namespace Stanza.Tests;

/// <summary>离线验证生产候选准入与排序，不调用搜索、取词或应用日志。</summary>
public class QqCandidateSelectionTests
{
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
    public async Task 手动跨源列表保留全部且同曲优先于高分同歌手噪声()
    {
        var query = new LyricsQuery("回旋", "歌手", 200000);
        var qq = new QqMusicLyricsProvider().ToCandidates(
        [
            Result("noise", "回旋木马", CompareHelper.MatchType.Perfect),
            Result("black", "黑夜回旋", CompareHelper.MatchType.Perfect),
            Result("related", "回旋 (Live)", CompareHelper.MatchType.Medium, ["童译"])
        ]);
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
    public void 专辑输入参与统一评分_同专辑优于冲突()
    {
        var query = new LyricsQuery("Song", "歌手", 200000, "专辑");
        var candidate = new QqMusicLyricsProvider().ToCandidates(
            [Result("song", "Song", CompareHelper.MatchType.VeryHigh, album: "专辑")])[0];
        Assert.True(LyricsCandidateRanker.Score(candidate, query).MatchScore >
                    LyricsCandidateRanker.Score(candidate, query with { Album = "乙唱片" }).MatchScore);
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
