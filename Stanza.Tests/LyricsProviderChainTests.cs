using Stanza.Models;
using Stanza.Services.Lyrics;
using Xunit;

namespace Stanza.Tests;

public class LyricsProviderChainTests
{
    private static readonly LyricsQuery Query = new("测试", "歌手", null);
    private static readonly LyricCandidate Candidate = new("test", "测试", "1", "候选", "歌手", null, null, null);

    [Fact]
    public async Task 慢源超时及异常不丢其它源已取得候选()
    {
        var slow = new Provider { Search = new TaskCompletionSource<IReadOnlyList<LyricCandidate>>().Task };
        var good = new Provider { Search = Task.FromResult<IReadOnlyList<LyricCandidate>>([Candidate]) };
        var broken = new Provider { Search = Task.FromException<IReadOnlyList<LyricCandidate>>(new InvalidOperationException("测试")) };
        var chain = new LyricsProviderChain(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(500), slow, good, broken);
        var results = await chain.GetCandidatesAsync(Query).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([LyricsCandidateRanker.Score(Candidate, Query)], results);
    }

    [Fact]
    public async Task 候选搜索并发且取消及时返回()
    {
        var slow = new Provider { Search = new TaskCompletionSource<IReadOnlyList<LyricCandidate>>().Task };
        var later = new Provider();
        var chain = new LyricsProviderChain(slow, later);
        using var cts = new CancellationTokenSource();
        var task = chain.GetCandidatesAsync(Query, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, later.SearchCalls);
    }

    [Fact]
    public async Task 总预算耗尽返回已有部分候选()
    {
        var good = new Provider { Search = Task.FromResult<IReadOnlyList<LyricCandidate>>([Candidate]) };
        var slow = new Provider { Search = new TaskCompletionSource<IReadOnlyList<LyricCandidate>>().Task };
        var chain = new LyricsProviderChain(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40), good, slow);
        Assert.Equal([LyricsCandidateRanker.Score(Candidate, Query)], await chain.GetCandidatesAsync(Query).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task 手选取词有界且支持取消()
    {
        var provider = new Provider();
        var chain = new LyricsProviderChain(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1), provider);
        Assert.Equal(LyricsLoadResult.NotFound, (await chain.LoadCandidateAsync(Candidate)).Status);
        using var cts = new CancellationTokenSource();
        var load = chain.LoadCandidateAsync(Candidate, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    private sealed class Provider : ILyricsProvider
    {
        public string Key => "test";
        public string Name => "测试";
        public int SearchCalls { get; private set; }
        public Task<IReadOnlyList<LyricCandidate>> Search { get; init; } = Task.FromResult<IReadOnlyList<LyricCandidate>>([]);
        public Task<LyricsResult> LoadAsync(LyricsQuery query) => Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));
        public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
        {
            SearchCalls++;
            return Search;
        }
        public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate) => new TaskCompletionSource<LyricsResult>().Task;
    }
}
