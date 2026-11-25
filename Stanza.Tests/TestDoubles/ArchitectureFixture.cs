using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stanza.Composition;
using Stanza.Core.Messaging;
using Stanza.Core.Ports;
using Stanza.Core.ReadModels;

namespace Stanza.Tests.TestDoubles;

/// <summary>运行真实 WPF Dispatcher 和应用装配，替换外部播放器与歌词网络入口。</summary>
internal sealed class ArchitectureFixture : IAsyncDisposable
{
    private readonly Thread _thread;
    public Dispatcher Dispatcher { get; }
    public IStateDispatcher StateDispatcher { get; }
    public ServiceProvider Container { get; }
    public TestPlaybackGateway Gateway { get; } = new();
    public TestLyricsProvider Provider { get; } = new();
    public PlaybackRuntime Runtime => Container.GetRequiredService<PlaybackRuntime>();
    public INowPlayingQueries Queries => Container.GetRequiredService<INowPlayingQueries>();
    public ICommandBus Commands => Container.GetRequiredService<ICommandBus>();

    public ArchitectureFixture()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            ready.SetResult(dispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Stanza.Tests.StateDispatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = ready.Task.GetAwaiter().GetResult();
        StateDispatcher = new WpfStateDispatcher(Dispatcher);
        var services = new ServiceCollection();
        ApplicationHost.RegisterApplication(services, StateDispatcher,
            Path.Combine(TestEnvironment.DataDirectory, Guid.NewGuid() + ".db"), Gateway);
        services.RemoveAll<ILyricsProvider>();
        services.AddSingleton<ILyricsProvider>(Provider);
        Container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public Task InStateAsync(Action action) => StateDispatcher.InvokeAsync(() =>
    {
        action();
        return Task.CompletedTask;
    });

    public async Task StartAsync()
    {
        await Runtime.StartAsync();
        await WaitUntilAsync(() => Queries.CreateLyricsSelectionContext() is not null);
    }

    public async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await StateDispatcher.InvokeAsync(() => Task.FromResult(predicate()), timeout.Token))
            await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Runtime.StopAsync();
            await Container.DisposeAsync();
        }
        finally
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

internal sealed class TestPlaybackGateway : IPlaybackGateway
{
    public TestPlaybackSession Session { get; private set; } = new();
    public event Action<PlaybackSelection?>? SelectionChanged;
    public event Action<string>? InitializationFailed { add { } remove { } }
    public string PlaybackSourceMode { get; private set; } = PlaybackSourceIds.Auto;
    public bool Disposed { get; private set; }
    public int RefreshCount { get; private set; }
    public Task InitializeAsync(CancellationToken cancellationToken)
    { Select(Session); return Task.CompletedTask; }
    public void RefreshSelection() => RefreshCount++;
    public void SetPlaybackSource(string? mode) => PlaybackSourceMode = mode ?? PlaybackSourceIds.Auto;
    public void Select(TestPlaybackSession session)
    {
        Session = session;
        var policy = PlaybackSourceRegistry.ResolveByKey(PlaybackSourceIds.QqMusic);
        SelectionChanged?.Invoke(new(session, policy, PlaybackSourceRegistry.ResolveAdapterByKey(policy.Key), "QQMusic.exe"));
    }
    public void Dispose() => Disposed = true;
}

internal sealed class TestPlaybackSession : IPlaybackSession
{
    public string Title { get; set; } = "测试歌曲";
    public RawPlaybackStatus Status { get; set; } = RawPlaybackStatus.Paused;
    public int PositionMs { get; set; } = 5000;
    public byte[]? Cover { get; set; }
    public int TimelineReads { get; private set; }
    public int StatusReads { get; private set; }
    public int MediaReads { get; private set; }
    public int ToggleCount { get; private set; }
    public int PreviousCount { get; private set; }
    public int NextCount { get; private set; }
    public event Action<IPlaybackSession>? MediaChanged;
    public event Action<IPlaybackSession>? TimelineChanged;
    public event Action<IPlaybackSession>? PlaybackChanged;
    public void RaiseMediaChanged() => MediaChanged?.Invoke(this);
    public void RaiseTimelineChanged() => TimelineChanged?.Invoke(this);
    public void RaisePlaybackChanged() => PlaybackChanged?.Invoke(this);
    public Action CaptureMediaCallback()
    {
        var callback = MediaChanged;
        return () => callback?.Invoke(this);
    }
    public RawPlaybackStatus GetStatus() { StatusReads++; return Status; }
    public PlaybackCapabilities GetCapabilities() => new(true, true, true, false, true);
    public PlaybackTimeline GetTimeline()
    {
        TimelineReads++;
        return new(TimeSpan.FromMilliseconds(PositionMs), TimeSpan.FromSeconds(200), DateTimeOffset.UtcNow);
    }
    public Task<PlaybackMedia> ReadMediaAsync(CancellationToken cancellationToken = default)
    {
        MediaReads++;
        return Task.FromResult(new PlaybackMedia(Title, "测试歌手", "测试专辑", _ => Task.FromResult(Cover)));
    }
    public Task PreviousAsync() { PreviousCount++; return Task.CompletedTask; }
    public Task TogglePlayPauseAsync() { ToggleCount++; return Task.CompletedTask; }
    public Task NextAsync() { NextCount++; return Task.CompletedTask; }
}

internal sealed class TestLyricsProvider : ILyricsProvider
{
    public string Key => "test";
    public string Name => "测试词源";
    public Func<LyricsQuery, Task<IReadOnlyList<LyricCandidate>>>? Search { get; set; }
    public Func<LyricCandidate, Task<LyricsResult>>? Load { get; set; }
    public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<LyricsResult> LoadAsync(LyricsQuery query) => Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));
    public Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
        => Search?.Invoke(query) ?? Task.FromResult<IReadOnlyList<LyricCandidate>>([Candidate(query)]);
    public Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        LoadStarted.TrySetResult();
        return Load?.Invoke(candidate) ?? Task.FromResult(new LyricsResult(LyricsLoadResult.Loaded, [new(0, "测试歌词")]));
    }
    public static LyricCandidate Candidate(LyricsQuery query) => new("test", "测试词源", query.Key,
        query.Title, query.Artist, query.Album, query.DurationMs, "Perfect");
}
