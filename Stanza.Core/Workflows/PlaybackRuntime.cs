using Stanza.Core.ReadModels;

namespace Stanza.Core.Workflows;

/// <summary>由 Host 启停的播放运行循环；100ms 位置采样与每秒会话刷新不依赖窗口 ticker。</summary>
internal sealed class PlaybackRuntime(
    PlaybackWorkflow playback, IPlaybackGateway gateway, IStateDispatcher dispatcher,
    WorkflowTasks tasks, NowPlayingProjection projection, CoverWorkflow cover, TimeProvider timeProvider)
{
    private Task _loop = Task.CompletedTask;
    private bool _started;
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        _started = true;
        await dispatcher.InvokeAsync(async () =>
        {
            // 初始化即可触发会话和缓存结果，先订阅再接入，避免漏掉首份状态。
            playback.Changed += projection.Publish;
            cover.Changed += projection.Publish;
            await playback.InitializeAsync();
            projection.Publish();
        }, cancellationToken);
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), timeProvider);
        var nextRefresh = timeProvider.GetUtcNow();
        try
        {
            while (await timer.WaitForNextTickAsync(tasks.Token))
            {
                await dispatcher.InvokeAsync(() =>
                {
                    var now = timeProvider.GetUtcNow();
                    if (now >= nextRefresh)
                    {
                        gateway.RefreshSelection();
                        nextRefresh = now.AddSeconds(1);
                    }
                    playback.AdvancePlayback(now.ToUnixTimeMilliseconds());
                    return Task.CompletedTask;
                }, tasks.Token);
            }
        }
        catch (OperationCanceledException) when (tasks.Token.IsCancellationRequested) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // 先阻止后续等待并作废请求，再退订来源；释放容器前仍要让异步收尾回到调度器。
        tasks.Cancel();
        await dispatcher.InvokeAsync(() =>
        {
            playback.Changed -= projection.Publish;
            cover.Changed -= projection.Publish;
            playback.Dispose();
            gateway.Dispose();
            return Task.CompletedTask;
        }, cancellationToken);
        await _loop;
        await tasks.DrainAsync();
    }
}
