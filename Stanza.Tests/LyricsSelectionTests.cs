using Stanza.Models;
using Stanza.Services.Lyrics;
using Xunit;

namespace Stanza.Tests;

public class LyricsSelectionTests
{
    private static readonly LyricsQuery Query = new("当前歌曲", "歌手", 180000);

    [Fact]
    public async Task 同曲迟到自动结果不能覆盖手选行和缓存且记忆可离线读取()
    {
        var store = new Cache();
        var choices = new InMemoryLyricsChoiceStore();
        var provider = new Provider();
        var coordinator = new LyricsCoordinator(store, choices, () => Query.Key, provider);
        var automatic = coordinator.LoadAsync(Query);
        await provider.AutoStarted.Task;
        var manual = coordinator.ApplyCandidateAsync(Query.Key, Candidate);
        provider.Manual.SetResult(Result("手选"));
        Assert.True(await manual);
        provider.Auto.SetResult(Result("自动旧词"));
        await automatic;
        Assert.Equal("手选", coordinator.Lyrics.Lines.Single().Text);
        Assert.Equal("手选", store.Items[Query.Key].Lines.Single().Text);

        var offline = new Provider();
        var restarted = new LyricsCoordinator(store, choices, () => Query.Key, offline);
        Assert.Equal(LyricsLoadResult.Loaded, await restarted.LoadAsync(Query));
        Assert.Equal("手选", restarted.Lyrics.Lines.Single().Text);
        Assert.False(offline.AutoStarted.Task.IsCompleted);
        Assert.Equal(0, offline.CandidateLoads);
    }

    [Fact]
    public async Task 旧记忆候选不复用错误自动缓存且补取后可离线重启()
    {
        var store = new Cache();
        store.Items[Query.Key] = new(LyricsLoadResult.Loaded, [new(0, "旧版错误自动词")]);
        var choices = new InMemoryLyricsChoiceStore();
        choices.Put(Query.Key, Candidate);
        var provider = new Provider();
        var coordinator = new LyricsCoordinator(store, choices, () => Query.Key, provider);

        var load = coordinator.LoadAsync(Query);
        Assert.Equal(1, provider.CandidateLoads);
        provider.Manual.SetResult(Result("正确候选词"));
        Assert.Equal(LyricsLoadResult.Loaded, await load);
        Assert.Equal("正确候选词", coordinator.Lyrics.Lines.Single().Text);
        Assert.Equal("正确候选词", store.Items[Query.Key].Lines.Single().Text);
        Assert.False(provider.AutoStarted.Task.IsCompleted);

        // 普通曲目缓存不能证明所属候选；离线重启必须读候选专用缓存。
        store.Items[Query.Key] = new(LyricsLoadResult.Loaded, [new(0, "仍不可信的自动词")]);
        var offline = new Provider();
        var restarted = new LyricsCoordinator(store, choices, () => Query.Key, offline);
        Assert.Equal(LyricsLoadResult.Loaded, await restarted.LoadAsync(Query).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("正确候选词", restarted.Lyrics.Lines.Single().Text);
        Assert.Equal(0, offline.CandidateLoads);
        Assert.False(offline.AutoStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task 取消手选后迟到结果不写行记忆缓存或完成状态()
    {
        var store = new Cache();
        var choices = new InMemoryLyricsChoiceStore();
        var provider = new Provider();
        var coordinator = new LyricsCoordinator(store, choices, () => Query.Key, provider);
        var statuses = new List<string>();
        var applied = 0;
        coordinator.StatusChanged += statuses.Add;
        coordinator.ResultApplied += (_, _) => applied++;
        using var cts = new CancellationTokenSource();
        var manual = coordinator.ApplyCandidateAsync(Query.Key, Candidate, cts.Token);
        cts.Cancel();
        provider.Manual.SetResult(Result("迟到"));
        Assert.False(await manual);
        Assert.Empty(coordinator.Lyrics.Lines);
        Assert.Empty(store.Items);
        Assert.False(choices.TryGet(Query.Key, out _));
        Assert.Equal(0, applied);
        Assert.Equal(["正在应用所选歌词…"], statuses);
    }

    [Fact]
    public async Task 已切到别歌的旧自动结果仍可缓存()
    {
        var store = new Cache();
        var provider = new Provider();
        var service = new LyricsService(store, provider);
        var load = service.LoadAsync(Query, service.Generation);
        await provider.AutoStarted.Task;
        service.InvalidateInFlight();
        provider.Auto.SetResult(Result("旧歌"));
        await load;
        Assert.Equal("旧歌", store.Items[Query.Key].Lines.Single().Text);
        Assert.Empty(service.Lines);
    }

    [Fact]
    public async Task 手选失败保留已有歌词且过期key不发起请求()
    {
        var store = new Cache();
        store.Items[Query.Key] = new LyricsCacheHit(LyricsLoadResult.Loaded, [new(0, "可用词")]);
        var provider = new Provider();
        var coordinator = new LyricsCoordinator(store, new InMemoryLyricsChoiceStore(), () => Query.Key, provider);
        await coordinator.LoadAsync(Query);
        Assert.False(await coordinator.ApplyCandidateAsync("旧key", Candidate));
        Assert.Equal(0, provider.CandidateLoads);
        var manual = coordinator.ApplyCandidateAsync(Query.Key, Candidate);
        provider.Manual.SetResult(new(LyricsLoadResult.NotFound, []));
        Assert.False(await manual);
        Assert.Equal("可用词", coordinator.Lyrics.Lines.Single().Text);
    }

    [Fact]
    public async Task 恢复探测挂起时手选成功不得再发布自动未找到()
    {
        var provider = new Provider();
        var coordinator = new LyricsCoordinator(new Cache(), new InMemoryLyricsChoiceStore(), () => Query.Key, provider);
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new TaskCompletionSource<RecoveryProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new List<string>();
        coordinator.StatusChanged += statuses.Add;
        coordinator.ScheduleRecovery(Query.Key, Query, _ =>
        {
            probeEntered.SetResult();
            return probe.Task;
        });
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var manual = coordinator.ApplyCandidateAsync(Query.Key, Candidate);
        provider.Manual.SetResult(Result("手选"));
        Assert.True(await manual);
        probe.SetResult(RecoveryProbe.Ready(Query));
        await coordinator.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("手选", coordinator.Lyrics.Lines.Single().Text);
        Assert.DoesNotContain("未找到歌词", statuses);
        Assert.False(provider.AutoStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task 取消恢复中的自动加载不写行或状态()
    {
        var provider = new Provider();
        var store = new Cache();
        var coordinator = new LyricsCoordinator(store, new InMemoryLyricsChoiceStore(), () => Query.Key, provider);
        var statuses = new List<string>();
        coordinator.StatusChanged += statuses.Add;
        coordinator.ScheduleRecovery(Query.Key, Query, _ => Task.FromResult(RecoveryProbe.Ready(Query)));
        await provider.AutoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.CancelRecovery();
        provider.Auto.SetResult(Result("迟到"));
        await coordinator.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(coordinator.Lyrics.Lines);
        Assert.Empty(store.Items);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task 纯音乐候选只临时显示不记忆或覆盖缓存()
    {
        var store = new Cache();
        var choices = new InMemoryLyricsChoiceStore();
        var provider = new Provider();
        var coordinator = new LyricsCoordinator(store, choices, () => Query.Key, provider);
        var manual = coordinator.ApplyCandidateAsync(Query.Key, Candidate);
        provider.Manual.SetResult(new(LyricsLoadResult.Instrumental, []));
        Assert.True(await manual);
        Assert.False(choices.TryGet(Query.Key, out _));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task 后发手选接管先发手选且只记忆后者()
    {
        var provider = new SequentialProvider();
        var choices = new InMemoryLyricsChoiceStore();
        var store = new Cache();
        var coordinator = new LyricsCoordinator(store, choices, () => Query.Key, provider);
        var first = coordinator.ApplyCandidateAsync(Query.Key, Candidate);
        var secondCandidate = Candidate with { Id = "2" };
        var second = coordinator.ApplyCandidateAsync(Query.Key, secondCandidate);
        provider.Second.SetResult(Result("后发"));
        Assert.True(await second);
        provider.First.SetResult(Result("先发"));
        Assert.False(await first);
        Assert.Equal("后发", coordinator.Lyrics.Lines.Single().Text);
        Assert.Equal("后发", store.Items[Query.Key].Lines.Single().Text);
        Assert.True(choices.TryGet(Query.Key, out var choice));
        Assert.Equal("2", choice!.Id);
    }

    private sealed class SequentialProvider : ILyricsProvider
    {
        public string Key => "test";
        public string Name => "测试";
        public TaskCompletionSource<LyricsResult> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LyricsResult> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LyricsResult> LoadAsync(LyricsQuery query) => Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));
        public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate) => candidate.Id == "1" ? First.Task : Second.Task;
    }

    private static LyricCandidate Candidate => new("test", "测试", "1", "候选", "歌手", null, null, null);
    private static LyricsResult Result(string text) => new(LyricsLoadResult.Loaded, [new LyricLine(0, text)]);

    private sealed class Provider : ILyricsProvider
    {
        public string Key => "test";
        public string Name => "测试";
        public TaskCompletionSource<LyricsResult> Auto { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LyricsResult> Manual { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AutoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CandidateLoads { get; private set; }
        public Task<LyricsResult> LoadAsync(LyricsQuery query)
        {
            AutoStarted.TrySetResult();
            return Auto.Task;
        }
        public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
        {
            CandidateLoads++;
            return Manual.Task;
        }
    }

    private sealed class Cache : ILyricsCacheStore
    {
        public Dictionary<string, LyricsCacheHit> Items { get; } = [];
        public void Initialize() { }
        public void Clear() => Items.Clear();
        public bool TryGet(string key, out LyricsLoadResult status, out List<LyricLine> lines)
        {
            var found = Items.TryGetValue(key, out var hit);
            status = found ? hit.Status : LyricsLoadResult.NotFound;
            lines = found ? hit.Lines : [];
            return found;
        }
        public void Put(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines)
            => Items[key] = new(status, [.. lines]);
        public Task<LyricsCacheHit?> GetAsync(string key)
            => Task.FromResult(Items.TryGetValue(key, out var hit) ? (LyricsCacheHit?)hit : null);
        public Task PutAsync(string key, LyricsLoadResult status, IReadOnlyList<LyricLine> lines)
        {
            Put(key, status, lines);
            return Task.CompletedTask;
        }
    }
}
