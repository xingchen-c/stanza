using Stanza.Core.Models;
using Stanza.Core.Lyrics;
using Xunit;

namespace Stanza.Tests;

[Collection("SqliteStorage")]
public sealed class LyricsCorrectionTests(SqliteStorageFixture fixture)
    : IClassFixture<SqliteStorageFixture>
{
    private static readonly LyricsQuery Query = new("当前歌曲", "歌手", 180000);
    private static readonly LyricCandidate ManualCandidate = new("test", "测试", "manual", "当前歌曲", "歌手", null, null, null);

    [Fact]
    public async Task 恢复自动匹配应移除本曲记忆并绕过内存和磁盘中的旧自动歌词()
    {
        var cache = fixture.Cache;
        cache.Put(LyricsLoader.AutomaticCacheKey(Query), LyricsLoadResult.Loaded, [new(0, "旧自动词")]);
        var choices = fixture.Choices;
        var provider = new Provider();
        var coordinator = new LyricsWorkflow(cache, choices, () => Query.Key, provider);
        await coordinator.LoadAsync(Query);
        choices.Put(Query.Key, ManualCandidate);
        choices.Put("其他歌曲", ManualCandidate);
        provider.Automatic.SetResult(Result("新自动词"));
        var applied = new List<string>();
        coordinator.ResultApplied += (key, _) => applied.Add(key);

        Assert.True(await coordinator.RestoreAutomaticAsync(Query));

        Assert.False(choices.TryGet(Query.Key, out _));
        Assert.True(choices.TryGet("其他歌曲", out _));
        Assert.Equal([Query.Key], applied);
        Assert.Equal("新自动词", coordinator.Lyrics.Lines.Single().Text);
        var restartedCache = new SqliteLyricsCacheStore(fixture.Database);
        var restartedChoices = new SqliteLyricsChoiceStore(fixture.Database);
        restartedCache.Initialize();
        restartedChoices.Initialize();
        var restarted = new LyricsWorkflow(restartedCache, restartedChoices, () => Query.Key, new Provider());
        Assert.Equal(LyricsLoadResult.Loaded, await restarted.LoadAsync(Query).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("新自动词", restarted.Lyrics.Lines.Single().Text);
    }

    [Fact]
    public async Task 恢复自动匹配后迟到的手选不能重新写回绑定()
    {
        var cache = new InMemoryLyricsCacheStore();
        var choices = new InMemoryLyricsChoiceStore();
        var provider = new Provider();
        var coordinator = new LyricsWorkflow(cache, choices, () => Query.Key, provider);
        var manual = coordinator.ApplyCandidateAsync(Query.Key, ManualCandidate);
        provider.Automatic.SetResult(Result("自动词"));

        Assert.True(await coordinator.RestoreAutomaticAsync(Query));
        provider.Manual.SetResult(Result("迟到的手选"));

        Assert.False(await manual);
        Assert.False(choices.TryGet(Query.Key, out _));
        Assert.Equal("自动词", coordinator.Lyrics.Lines.Single().Text);
        Assert.False(cache.TryGet(LyricsLoader.CandidateCacheKey(Query.Key, ManualCandidate), out _, out _));
    }

    [Fact]
    public async Task 过期曲目不能恢复自动匹配且请求途中切歌不能污染新曲显示()
    {
        var choices = new InMemoryLyricsChoiceStore();
        choices.Put(Query.Key, ManualCandidate);
        var currentKey = "其他歌曲";
        var provider = new Provider();
        var coordinator = new LyricsWorkflow(new InMemoryLyricsCacheStore(), choices, () => currentKey, provider);
        Assert.False(await coordinator.RestoreAutomaticAsync(Query));
        Assert.True(choices.TryGet(Query.Key, out _));
        Assert.Equal(0, provider.SearchCount);

        currentKey = Query.Key;
        var applied = 0;
        coordinator.ResultApplied += (_, _) => applied++;
        var restore = coordinator.RestoreAutomaticAsync(Query);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        currentKey = "其他歌曲";
        coordinator.Lyrics.InvalidateInFlight();
        coordinator.Lyrics.Clear();
        provider.Automatic.SetResult(Result("旧曲自动词"));

        Assert.False(await restore);
        Assert.Empty(coordinator.Lyrics.Lines);
        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task 重新匹配未找到时应清掉错误歌词且不恢复旧绑定或旧自动缓存()
    {
        var cache = new InMemoryLyricsCacheStore();
        cache.Put(LyricsLoader.AutomaticCacheKey(Query), LyricsLoadResult.Loaded, [new(0, "错误词")]);
        var choices = new InMemoryLyricsChoiceStore();
        choices.Put(Query.Key, ManualCandidate);
        var provider = new Provider();
        provider.Automatic.SetResult(new(LyricsLoadResult.NotFound, []));
        var coordinator = new LyricsWorkflow(cache, choices, () => Query.Key, provider);

        Assert.True(await coordinator.RestoreAutomaticAsync(Query));

        Assert.Empty(coordinator.Lyrics.Lines);
        Assert.False(choices.TryGet(Query.Key, out _));
        Assert.False(cache.TryGet(LyricsLoader.AutomaticCacheKey(Query), out _, out _));
    }

    private static LyricsResult Result(string text) => new(LyricsLoadResult.Loaded, [new(0, text)]);

    private sealed class Provider : ILyricsProvider
    {
        public string Key => "test";
        public string Name => "测试";
        public int SearchCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LyricsResult> Automatic { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LyricsResult> Manual { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LyricsResult> LoadAsync(LyricsQuery query) => Automatic.Task;
        public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
        {
            SearchCount++;
            Started.TrySetResult();
            return Task.FromResult<IReadOnlyList<LyricCandidate>>([
                new(Key, Name, "auto", query.Title, query.Artist, query.Album, query.DurationMs, null)]);
        }
        public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
            => candidate.Id == "manual" ? Manual.Task : Automatic.Task;
    }
}
