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
