using System.Collections.Concurrent;
using Stanza.Models;
using Stanza.Services.Lyrics;
using Stanza.Tests.TestDoubles;
using Xunit;

namespace Stanza.Tests;

public class LyricsRecoveryTests
{
    private static readonly LyricsQuery Query = new("测试歌曲", "测试歌手", 180_000);

    [Fact]
    public async Task 暂停期间补抓应重试而非终止()
    {
        var provider = new CountingLyricsProvider(LyricsLoadResult.NotFound);
        var coordinator = CreateCoordinator(
            provider,
            () => Query.Key,
            recoveryLifetimeMs: 1000,
            idlePollIntervalMs: 10,
            recoveryBackoffMs: [10]);
        var probes = new ConcurrentQueue<RecoveryProbe>([
            RecoveryProbe.Retry(),
            RecoveryProbe.Ready(Query),
            RecoveryProbe.Abort()
        ]);

        coordinator.ScheduleRecovery(
            Query.Key,
            Query,
            _ => Task.FromResult(
                probes.TryDequeue(out var probe) ? probe : RecoveryProbe.Abort()));

        await WaitUntilAsync(() => provider.LoadCount >= 2, TimeSpan.FromSeconds(2));
        coordinator.CancelRecovery();

        Assert.True(provider.LoadCount >= 2);
    }

    [Fact]
    public async Task 探测持续不可读应最终落地为未找到而非静默退出()
    {
        var provider = new CountingLyricsProvider(LyricsLoadResult.NotFound);
        var coordinator = CreateCoordinator(
            provider,
            () => Query.Key,
            recoveryLifetimeMs: 40,
            idlePollIntervalMs: 10,
            recoveryBackoffMs: [10]);
        var status = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += text =>
        {
            if (text == "未找到歌词")
                status.TrySetResult(text);
        };

        coordinator.ScheduleRecovery(Query.Key, Query, _ => Task.FromResult(RecoveryProbe.Retry()));

        Assert.Equal("未找到歌词", await status.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task 探测返回终止时不应再改动状态()
    {
        var provider = new CountingLyricsProvider(LyricsLoadResult.NotFound);
        var coordinator = CreateCoordinator(
            provider,
            () => Query.Key,
            recoveryLifetimeMs: 500,
            idlePollIntervalMs: 10,
            recoveryBackoffMs: [10]);
        var resultCount = 0;
        var statusCount = 0;
        coordinator.ResultApplied += (_, _) => Interlocked.Increment(ref resultCount);
        coordinator.StatusChanged += _ => Interlocked.Increment(ref statusCount);

        coordinator.ScheduleRecovery(Query.Key, Query, _ => Task.FromResult(RecoveryProbe.Abort()));
        await Task.Delay(150);

        Assert.Equal(0, resultCount);
        Assert.Equal(0, statusCount);
    }

    [Fact]
    public async Task 迟到的候选应用应整体放弃且不产生任何副作用()
    {
        var provider = new GatedLyricsProvider();
        var choiceStore = new RecordingLyricsChoiceStore();
        var currentKey = Query.Key;
        var coordinator = CreateCoordinator(
            provider,
            () => currentKey,
            choiceStore: choiceStore,
            recoveryLifetimeMs: 500,
            idlePollIntervalMs: 10,
            recoveryBackoffMs: [10]);
        var candidate = new LyricCandidate(
            provider.Key,
            provider.Name,
            "old-candidate",
            "旧候选",
            Query.Artist,
            null,
            null,
            null);
        var gate = provider.Gate(candidate.Title);
        var resultCount = 0;
        var statuses = new List<string>();
        coordinator.ResultApplied += (_, _) => Interlocked.Increment(ref resultCount);
        coordinator.StatusChanged += statuses.Add;

        var applyTask = coordinator.ApplyCandidateAsync(Query.Key, candidate);
        currentKey = "新歌\u0001新歌手";
        coordinator.Lyrics.InvalidateInFlight();
        gate.SetResult(new LyricsResult(
            LyricsLoadResult.Loaded,
            [new LyricLine(0, "旧候选第一行")]));

        Assert.False(await applyTask);
        Assert.Empty(choiceStore.WrittenKeys);
        Assert.Equal(0, resultCount);
        Assert.Equal(["正在应用所选歌词…"], statuses);
    }

    [Fact]
    public async Task 取消恢复循环属于正常终止()
    {
        var provider = new CountingLyricsProvider(LyricsLoadResult.NotFound);
        var coordinator = CreateCoordinator(
            provider,
            () => Query.Key,
            recoveryLifetimeMs: 1000,
            idlePollIntervalMs: 10,
            recoveryBackoffMs: [10]);
        var resultCount = 0;
        coordinator.ResultApplied += (_, _) => Interlocked.Increment(ref resultCount);

        coordinator.ScheduleRecovery(Query.Key, Query, _ => Task.FromResult(RecoveryProbe.Retry()));
        coordinator.CancelRecovery();
        await Task.Delay(150);

        Assert.Equal(0, resultCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 歧义候选等待可信时长且最终状态保留歧义或确定歌词(bool durationBecomesReady)
    {
        var provider = new AmbiguousProvider();
        var query = Query with { DurationMs = null };
        var coordinator = CreateCoordinator(provider, () => Query.Key, 1000, 10, [10]);
        var results = new List<LyricsLoadResult>();
        coordinator.ResultApplied += (_, result) => results.Add(result);
        var probeCount = 0;

        coordinator.ScheduleRecovery(Query.Key, query, _ => Task.FromResult(RecoveryProbe.Ready(
            durationBecomesReady && ++probeCount > 1 ? Query : query)));
        await coordinator.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LyricsLoadResult.Ambiguous, results[0]);
        Assert.Equal(durationBecomesReady ? LyricsLoadResult.Loaded : LyricsLoadResult.Ambiguous, results[^1]);
        Assert.Equal(durationBecomesReady ? ["确定版本"] : [], coordinator.Lyrics.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task 歧义后的元数据持续不可读不应改称未找到歌词()
    {
        var coordinator = CreateCoordinator(new AmbiguousProvider(), () => Query.Key, 40, 10, [10]);
        var query = Query with { DurationMs = null };
        var results = new List<LyricsLoadResult>();
        coordinator.ResultApplied += (_, result) => results.Add(result);
        var firstProbe = true;
        coordinator.ScheduleRecovery(Query.Key, query, _ =>
        {
            var probe = firstProbe ? RecoveryProbe.Ready(query) : RecoveryProbe.Retry();
            firstProbe = false;
            return Task.FromResult(probe);
        });

        await coordinator.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal(LyricsLoadResult.Ambiguous, result));
    }

    private sealed class AmbiguousProvider : ILyricsProvider
    {
        public string Key => "Ambiguous";
        public string Name => "歧义测试源";
        public Task<LyricsResult> LoadAsync(LyricsQuery query) => throw new InvalidOperationException();
        public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
            => Task.FromResult<IReadOnlyList<LyricCandidate>>([
                new(Key, Name, "180", Query.Title, Query.Artist, "甲录音", 180000, null),
                new(Key, Name, "200", Query.Title, Query.Artist, "乙录音", 200000, null)]);
        public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
            => Task.FromResult(new LyricsResult(LyricsLoadResult.Loaded, [new(0, "确定版本")]));
    }

    private static LyricsCoordinator CreateCoordinator(
        ILyricsProvider provider,
        Func<string> currentKey,
        int recoveryLifetimeMs,
        int idlePollIntervalMs,
        int[] recoveryBackoffMs,
        ILyricsChoiceStore? choiceStore = null)
        => new(
            new RecordingLyricsCacheStore(),
            choiceStore ?? new RecordingLyricsChoiceStore(),
            currentKey,
            recoveryLifetimeMs,
            idlePollIntervalMs,
            recoveryBackoffMs,
            provider);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.True(condition(), $"等待条件超时：{timeout}");
    }
}
