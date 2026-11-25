using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Stanza.Composition;
using Stanza.Core.Messaging;
using Stanza.Core.Ports;
using Stanza.Core.ReadModels;
using Stanza.Tests.TestDoubles;
using Xunit;

namespace Stanza.Tests;

[Collection("SqliteStorage")]
public sealed class ArchitectureTests
{
    [Fact]
    public async Task 容器共享状态并按命令解析独立处理器()
    {
        await using var fixture = new ArchitectureFixture();
        var container = fixture.Container;
        Assert.Same(container.GetRequiredService<PlaybackContext>(), container.GetRequiredService<PlaybackContext>());
        Assert.Same(container.GetRequiredService<LyricsContext>(), container.GetRequiredService<LyricsLoader>().Context);
        Assert.NotSame(container.GetRequiredService<ICommandHandler<TogglePlayback, bool>>(),
            container.GetRequiredService<ICommandHandler<TogglePlayback, bool>>());
        var registrations = container.GetServices<CommandRegistration>().ToArray();
        var commandTypes = typeof(ICommand<>).Assembly.GetTypes().Where(type =>
            !type.IsAbstract && type.GetInterfaces().Any(contract =>
                contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICommand<>))).ToArray();
        Assert.NotEmpty(commandTypes);
        Assert.Equal(commandTypes.OrderBy(type => type.FullName),
            registrations.Select(registration => registration.CommandType).OrderBy(type => type.FullName));
        foreach (var commandType in commandTypes)
        {
            Assert.Single(registrations, registration => registration.CommandType == commandType);
            var contract = commandType.GetInterfaces().Single(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICommand<>));
            var handlerType = typeof(ICommandHandler<,>).MakeGenericType(commandType, contract.GetGenericArguments()[0]);
            Assert.Single(container.GetServices(handlerType));
        }
        using var host = ApplicationHost.Build(fixture.Dispatcher);
        Assert.NotNull(host.Services.GetRequiredService<WindowFactory>());
    }

    [Fact]
    public async Task 没有窗口也持续采样且停止后不再访问播放器()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().Sample.RawPositionMs == 5000);
        await fixture.InStateAsync(() => fixture.Gateway.Session.PositionMs = 8000);
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().Sample.RawPositionMs == 8000);
        await fixture.Runtime.StopAsync();
        var count = fixture.Gateway.Session.TimelineReads;
        await Task.Delay(250);
        Assert.Equal(count, fixture.Gateway.Session.TimelineReads);
        Assert.True(fixture.Gateway.Disposed);
    }

    [Fact]
    public async Task 读取快照和选择上下文不会采样或推进世代()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        await fixture.InStateAsync(() =>
        {
            var snapshot = fixture.Queries.GetSnapshot();
            var reads = fixture.Gateway.Session.TimelineReads;
            var statusReads = fixture.Gateway.Session.StatusReads;
            var generation = fixture.Container.GetRequiredService<LyricsContext>().Generation;
            for (var i = 0; i < 100; i++)
            {
                Assert.Same(snapshot, fixture.Queries.GetSnapshot());
                var context = fixture.Queries.CreateLyricsSelectionContext()!;
                Assert.True(fixture.Queries.IsLyricsSelectionCurrent(context));
                Assert.Equal(0, fixture.Queries.GetLyricOffset(context));
            }
            Assert.Equal(reads, fixture.Gateway.Session.TimelineReads);
            Assert.Equal(statusReads, fixture.Gateway.Session.StatusReads);
            Assert.Equal(generation, fixture.Container.GetRequiredService<LyricsContext>().Generation);
        });
    }

    [Fact]
    public async Task 播放与偏移命令通过真实总线处理且过期上下文不生效()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        var context = await fixture.StateDispatcher.InvokeAsync(() => Task.FromResult(fixture.Queries.CreateLyricsSelectionContext()!));
        Assert.True(await fixture.Commands.SendAsync(new TogglePlayback()));
        Assert.Equal(1, fixture.Gateway.Session.ToggleCount);
        Assert.True(await fixture.Commands.SendAsync(new PreviousTrack()));
        Assert.True(await fixture.Commands.SendAsync(new NextTrack()));
        Assert.Equal(1, fixture.Gateway.Session.PreviousCount);
        Assert.Equal(1, fixture.Gateway.Session.NextCount);
        Assert.True(await fixture.Commands.SendAsync(new SetLyricOffset(context, 300)));
        Assert.Equal(300, fixture.Queries.GetSnapshot().LyricOffsetMs);
        Assert.False(await fixture.Commands.SendAsync(new SetLyricOffset(context, 10001)));
        await fixture.InStateAsync(() =>
        {
            fixture.Gateway.Session.Title = "另一首";
            fixture.Gateway.Session.RaiseMediaChanged();
        });
        await fixture.WaitUntilAsync(() => fixture.Queries.CreateLyricsSelectionContext()?.Key != context.Key);
        Assert.False(await fixture.Commands.SendAsync(new SetLyricOffset(context, 600)));
        Assert.Equal(300, fixture.Container.GetRequiredService<ILyricsOffsetStore>().Get(context.Key));
    }

    [Fact]
    public async Task 已取消的命令不会调用播放器()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Commands.SendAsync(new TogglePlayback(), cancellation.Token));
        Assert.Equal(0, fixture.Gateway.Session.ToggleCount);
    }

    [Fact]
    public async Task 歌词网络等待期间后续播放命令仍可执行()
    {
        await using var fixture = new ArchitectureFixture();
        var result = new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Load = _ => result.Task;
        await fixture.StartAsync();
        var context = fixture.Queries.CreateLyricsSelectionContext()!;
        var apply = fixture.Commands.SendAsync(new ApplyLyricsCandidate(context, TestLyricsProvider.Candidate(context.Query)));
        await fixture.Provider.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(apply.IsCompleted);
        Assert.True(await fixture.Commands.SendAsync(new TogglePlayback()).WaitAsync(TimeSpan.FromSeconds(2)));
        result.SetResult(new(LyricsLoadResult.Loaded, [new(0, "手选歌词")]));
        Assert.True(await apply);
        Assert.Equal("手选歌词", fixture.Queries.GetSnapshot().Lines.Single().Text);
    }

    [Fact]
    public async Task 歌词候选查询跨曲后丢弃结果且不改当前歌词()
    {
        await using var fixture = new ArchitectureFixture();
        var result = new TaskCompletionSource<IReadOnlyList<LyricCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Search = _ => { started.SetResult(); return result.Task; };
        await fixture.StartAsync();
        var context = fixture.Queries.CreateLyricsSelectionContext()!;
        var search = fixture.StateDispatcher.InvokeAsync(() => fixture.Queries.GetLyricCandidatesAsync(context));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.InStateAsync(() =>
        {
            fixture.Gateway.Session.Title = "新的歌曲";
            fixture.Gateway.Session.RaiseMediaChanged();
        });
        await fixture.WaitUntilAsync(() => fixture.Queries.CreateLyricsSelectionContext()?.Key != context.Key);
        result.SetResult([TestLyricsProvider.Candidate(context.Query)]);
        Assert.Empty(await search);
        Assert.Empty(fixture.Queries.GetSnapshot().Lines);
    }

    [Fact]
    public async Task 发布的歌词与逐字集合不可修改且不随原列表变化()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        await fixture.InStateAsync(() =>
        {
            var words = new List<LyricWord> { new("原词", 0, 1000) };
            var lines = new List<LyricLine> { new(0, "原歌词", words: words) };
            var context = fixture.Queries.CreateLyricsSelectionContext()!;
            fixture.Container.GetRequiredService<LyricsContext>().Apply(lines, context.Key);
            fixture.Container.GetRequiredService<NowPlayingProjection>().Publish();
            var snapshot = fixture.Queries.GetSnapshot();
            words.Clear();
            lines.Clear();
            Assert.Equal("原词", snapshot.Lines[0].Words![0].Text);
            Assert.Throws<NotSupportedException>(() => ((IList<LyricLine>)snapshot.Lines).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<LyricWord>)snapshot.Lines[0].Words!).Clear());
            fixture.Container.GetRequiredService<LyricsContext>().Apply([new(0, "新歌词")], context.Key);
            fixture.Container.GetRequiredService<NowPlayingProjection>().Publish();
            Assert.Equal("原歌词", snapshot.Lines[0].Text);
            Assert.Equal("新歌词", fixture.Queries.GetSnapshot().Lines[0].Text);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 新曲目快照不会夹带旧曲歌词或使用旧位置(bool replaceSession)
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().Sample.ShouldRender);
        var snapshots = new List<NowPlayingSnapshot>();
        await fixture.InStateAsync(() =>
        {
            var context = fixture.Queries.CreateLyricsSelectionContext()!;
            fixture.Container.GetRequiredService<LyricsContext>().Apply([new(0, "旧歌词")], context.Key);
            fixture.Queries.Changed += snapshots.Add;
            if (replaceSession)
                fixture.Gateway.Select(new TestPlaybackSession { Title = "新会话歌曲" });
            else
            {
                fixture.Gateway.Session.Title = "新会话歌曲";
                fixture.Gateway.Session.RaiseMediaChanged();
            }
        });
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().CurrentTitle == "新会话歌曲");
        await fixture.InStateAsync(() => fixture.Queries.Changed -= snapshots.Add);
        // 回调只收集证据，断言留在测试中，避免被业务流程的降级捕获吞掉。
        Assert.NotEmpty(snapshots);
        if (replaceSession)
        {
            var waiting = snapshots.Where(snapshot => snapshot.Track is null).ToArray();
            Assert.NotEmpty(waiting);
            Assert.All(waiting, snapshot =>
            {
                Assert.False(snapshot.Sample.ShouldRender);
                Assert.Empty(snapshot.Lines);
            });
        }
        var confirmed = snapshots.Where(snapshot => snapshot.CurrentTitle == "新会话歌曲").ToArray();
        Assert.NotEmpty(confirmed);
        Assert.False(confirmed[0].Sample.ShouldRender);
        Assert.All(confirmed, snapshot => Assert.Empty(snapshot.Lines));
    }

    [Fact]
    public async Task 旧会话迟到通知不会重新读取当前会话或接受旧选择上下文()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        var old = fixture.Gateway.Session;
        var callback = old.CaptureMediaCallback();
        var context = fixture.Queries.CreateLyricsSelectionContext()!;
        var current = new TestPlaybackSession { Title = "新会话歌曲" };
        await fixture.InStateAsync(() => fixture.Gateway.Select(current));
        var reads = current.MediaReads;
        await fixture.InStateAsync(callback);
        await fixture.InStateAsync(() =>
        {
            Assert.Equal(reads, current.MediaReads);
            Assert.Equal("新会话歌曲", fixture.Queries.GetSnapshot().CurrentTitle);
            Assert.False(fixture.Queries.IsLyricsSelectionCurrent(context));
        });
    }

    [Fact]
    public async Task 恢复自动匹配命令移除本曲手选记忆并发布新自动歌词()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        var context = fixture.Queries.CreateLyricsSelectionContext()!;
        var candidate = TestLyricsProvider.Candidate(context.Query);
        Assert.True(await fixture.Commands.SendAsync(new ApplyLyricsCandidate(context, candidate)));
        var choices = fixture.Container.GetRequiredService<ILyricsChoiceStore>();
        Assert.True(choices.TryGet(context.Key, out _));
        choices.Put("其他歌曲", candidate);
        fixture.Provider.Load = _ => Task.FromResult(new LyricsResult(LyricsLoadResult.Loaded, [new(0, "新自动歌词")]));

        Assert.True(await fixture.Commands.SendAsync(new RestoreAutomaticLyrics(context)));

        Assert.Equal("新自动歌词", fixture.Queries.GetSnapshot().Lines.Single().Text);
        Assert.False(choices.TryGet(context.Key, out _));
        Assert.True(choices.TryGet("其他歌曲", out _));
        var cache = fixture.Container.GetRequiredService<ILyricsCacheStore>();
        Assert.True(cache.TryGet(LyricsLoader.AutomaticCacheKey(context.Query), out _, out var lines));
        Assert.Equal("新自动歌词", lines.Single().Text);
        await fixture.InStateAsync(() => fixture.Gateway.Select(new TestPlaybackSession { Title = "另一首" }));
        choices.Put(context.Key, candidate);
        Assert.False(await fixture.Commands.SendAsync(new RestoreAutomaticLyrics(context)));
        Assert.True(choices.TryGet(context.Key, out _));
    }

    [Fact]
    public async Task 清除歌词数据命令清除缓存与记忆并保留当前歌词和单曲偏移()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        var context = fixture.Queries.CreateLyricsSelectionContext()!;
        var candidate = TestLyricsProvider.Candidate(context.Query);
        Assert.True(await fixture.Commands.SendAsync(new ApplyLyricsCandidate(context, candidate)));
        Assert.True(await fixture.Commands.SendAsync(new SetLyricOffset(context, 300)));
        var cache = fixture.Container.GetRequiredService<ILyricsCacheStore>();
        var choices = fixture.Container.GetRequiredService<ILyricsChoiceStore>();
        cache.Put("其他歌曲", LyricsLoadResult.Loaded, [new(0, "其他歌词")]);
        choices.Put("其他歌曲", candidate);
        Assert.True(cache.TryGet(LyricsLoader.CandidateCacheKey(context.Key, candidate), out _, out _));

        Assert.True(await fixture.Commands.SendAsync(new ClearLyricsData()));

        Assert.False(cache.TryGet(context.Key, out _, out _));
        Assert.False(cache.TryGet(LyricsLoader.CandidateCacheKey(context.Key, candidate), out _, out _));
        Assert.False(cache.TryGet("其他歌曲", out _, out _));
        Assert.False(choices.TryGet(context.Key, out _));
        Assert.False(choices.TryGet("其他歌曲", out _));
        Assert.Equal(300, fixture.Queries.GetSnapshot().LyricOffsetMs);
        Assert.Equal("测试歌词", fixture.Queries.GetSnapshot().Lines.Single().Text);
        fixture.Provider.Load = _ => Task.FromResult(new LyricsResult(LyricsLoadResult.Loaded, [new(0, "重新获取的歌词")]));
        Assert.True(await fixture.Commands.SendAsync(new ApplyLyricsCandidate(context, candidate)));
        Assert.Equal("重新获取的歌词", fixture.Queries.GetSnapshot().Lines.Single().Text);
    }

    [Fact]
    public async Task 播放源选择命令传递来源且空值恢复自动模式()
    {
        await using var fixture = new ArchitectureFixture();
        await fixture.StartAsync();
        Assert.True(await fixture.Commands.SendAsync(new SelectPlaybackSource(PlaybackSourceIds.NetEase)));
        Assert.Equal(PlaybackSourceIds.NetEase, fixture.Gateway.PlaybackSourceMode);
        Assert.True(await fixture.Commands.SendAsync(new SelectPlaybackSource(null)));
        Assert.Equal(PlaybackSourceIds.Auto, fixture.Gateway.PlaybackSourceMode);
    }

    [Fact]
    public async Task 媒体区开关命令清除封面并允许重新启用后加载()
    {
        await using var fixture = new ArchitectureFixture();
        fixture.Gateway.Session.Cover = [1, 2, 3];
        await fixture.StartAsync();
        Assert.Empty(fixture.Queries.GetSnapshot().Cover);
        Assert.True(await fixture.Commands.SendAsync(new SetMediaControls(true)));
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().Cover.SequenceEqual(new byte[] { 1, 2, 3 }));
        await fixture.StateDispatcher.InvokeAsync(async () =>
        {
            Assert.True(await fixture.Commands.SendAsync(new SetMediaControls(false)));
            Assert.Empty(fixture.Queries.GetSnapshot().Cover);
        });
        Assert.True(await fixture.Commands.SendAsync(new SetMediaControls(true)));
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().Cover.SequenceEqual(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task 拒收封面命令发布空封面并允许同曲重试相同图片()
    {
        await using var fixture = new ArchitectureFixture();
        fixture.Gateway.Session.Cover = [1, 2, 3];
        await fixture.StartAsync();
        Assert.True(await fixture.Commands.SendAsync(new SetMediaControls(true)));
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().Cover.SequenceEqual(new byte[] { 1, 2, 3 }));
        await fixture.StateDispatcher.InvokeAsync(async () =>
        {
            Assert.True(await fixture.Commands.SendAsync(new RejectCover()));
            Assert.Empty(fixture.Queries.GetSnapshot().Cover);
        });
        await fixture.WaitUntilAsync(() => fixture.Queries.GetSnapshot().Cover.SequenceEqual(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task 应用停止也会取消并等待挂起的候选查询()
    {
        await using var fixture = new ArchitectureFixture();
        var result = new TaskCompletionSource<IReadOnlyList<LyricCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Search = _ => { started.SetResult(); return result.Task; };
        await fixture.StartAsync();
        var context = fixture.Queries.CreateLyricsSelectionContext()!;
        var search = fixture.StateDispatcher.InvokeAsync(() => fixture.Queries.GetLyricCandidatesAsync(context));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        result.SetResult([]);
    }

    [Fact]
    public async Task 应用停止取消挂起命令并等待已登记工作完成()
    {
        await using var fixture = new ArchitectureFixture();
        var result = new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Load = _ => result.Task;
        await fixture.StartAsync();
        var context = fixture.Queries.CreateLyricsSelectionContext()!;
        var apply = fixture.Commands.SendAsync(new ApplyLyricsCandidate(context, TestLyricsProvider.Candidate(context.Query)));
        await fixture.Provider.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = fixture.Container.GetRequiredService<WorkflowTasks>();
        _ = tasks.Run(() => gate.Task);
        var stopping = fixture.Runtime.StopAsync();
        Assert.True(tasks.Token.IsCancellationRequested);
        Assert.False(stopping.IsCompleted);
        gate.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(await apply);
        result.SetResult(new(LyricsLoadResult.Loaded, [new(0, "迟到歌词")]));
        Assert.Empty(fixture.Queries.GetSnapshot().Lines);
        Assert.False(fixture.Container.GetRequiredService<ILyricsChoiceStore>().TryGet(context.Key, out _));
    }

    [Fact]
    public async Task 迟到封面不会发布到下一首且适配器不跨实例共享状态()
    {
        await using var fixture = new ArchitectureFixture();
        var gate = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = 1L;
        var media = new PlaybackMedia("歌", "歌手", "", _ => gate.Task);
        var cover = fixture.Container.GetRequiredService<CoverWorkflow>();
        await fixture.InStateAsync(() => cover.SetEnabled(true));
        var pending = fixture.StateDispatcher.InvokeAsync(() => cover.RefreshWithRetryAsync(
            fixture.Gateway.Session, media, "first", revision, () => true, () => revision));
        await fixture.InStateAsync(() => revision = 2);
        gate.SetResult([1, 2, 3]);
        await pending;
        Assert.Empty(cover.Data);
        var first = PlaybackSourceRegistry.ResolveAdapterByKey(PlaybackSourceIds.QqMusic);
        var second = PlaybackSourceRegistry.ResolveAdapterByKey(PlaybackSourceIds.QqMusic);
        var request = new PlaybackCoverRequest("same", media, 1000);
        Assert.Equal(PlaybackCoverAction.Available, (await first.GetCoverAsync(request, default)).Action);
        Assert.Equal(PlaybackCoverAction.Available, (await second.GetCoverAsync(request, default)).Action);
    }

    [Fact]
    public void 数据库不可用时三种存储整体降级并保持可用()
    {
        var path = Path.Combine(TestEnvironment.DataDirectory, Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        var backend = new StorageBackend(path);
        var query = new LyricsQuery("降级歌曲", "歌手", null);
        backend.LyricsCache.Put(query.Key, LyricsLoadResult.Loaded, [new(0, "内存歌词")]);
        backend.LyricsChoices.Put(query.Key, TestLyricsProvider.Candidate(query));
        Assert.True(backend.LyricsOffsets.Set(query.Key, 100));
        Assert.True(backend.LyricsCache.TryGet(query.Key, out _, out var lines));
        Assert.Equal("内存歌词", lines.Single().Text);
        Assert.True(backend.LyricsChoices.TryGet(query.Key, out _));
        Assert.Equal(100, backend.LyricsOffsets.Get(query.Key));
    }
}
