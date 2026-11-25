using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stanza.Core.Workflows;
using Stanza.Infrastructure.Lyrics;
using Stanza.Infrastructure.Playback;
using Stanza.Infrastructure.Settings;
using Stanza.Infrastructure.Storage;

namespace Stanza.Composition;

/// <summary>唯一的生产组合根；容器只在此处及命令分发边界解析，不传入业务内核。</summary>
internal static class ApplicationHost
{
    internal static IHost Build(Dispatcher dispatcher)
    {
        AppLog.BindCore();
        // 延续现有 settings.json 与 AppLog 入口，不额外启用宿主默认配置和日志来源。
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        }));
        RegisterApplication(builder.Services, new WpfStateDispatcher(dispatcher));
        builder.Services.AddSingleton<IHostLifetime, WpfHostLifetime>();
        builder.Services.AddSingleton<IHostedService>(sp => new HostedPlayback(sp.GetRequiredService<PlaybackRuntime>()));
        builder.Services.AddSingleton(sp => new WindowFactory(
            sp.GetRequiredService<ICommandBus>(), sp.GetRequiredService<INowPlayingQueries>(),
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IAutoStart>()));
        return builder.Build();
    }

    internal static void RegisterApplication(IServiceCollection services, IStateDispatcher dispatcher,
        string? databasePath = null, IPlaybackGateway? gateway = null)
    {
        services.AddSingleton(dispatcher);
        services.AddSingleton(TimeProvider.System);
        // 状态与工作流共享应用生命周期；窗口重建不能另起一套播放或歌词状态。
        services.AddSingleton(_ => new WorkflowTasks());
        services.AddSingleton(_ => new PlaybackContext());
        services.AddSingleton(_ => new LyricsContext());
        // 三种存储经同一个后端初始化，数据库失败时整体降级，避免各自选择不同后端。
        services.AddSingleton(_ => new StorageBackend(databasePath));
        services.AddSingleton(sp => sp.GetRequiredService<StorageBackend>().LyricsCache);
        services.AddSingleton(sp => sp.GetRequiredService<StorageBackend>().LyricsChoices);
        services.AddSingleton(sp => sp.GetRequiredService<StorageBackend>().LyricsOffsets);
        services.AddSingleton<ISettingsStore>(_ => new SettingsFile());
        services.AddSingleton<IAutoStart>(_ => new WindowsAutoStart());
        // 注册顺序仅作同等级候选的最终兜底，不能替代统一匹配规则。
        services.AddSingleton<ILyricsProvider>(_ => new QqMusicLyricsProvider());
        services.AddSingleton<ILyricsProvider>(_ => new NeteaseLyricsProvider());
        services.AddSingleton<ILyricsProvider>(_ => new LrclibLyricsProvider());
        services.AddSingleton(sp => new LyricsProviderChain(sp.GetServices<ILyricsProvider>().ToArray()));
        services.AddSingleton(sp => new LyricsLoader(sp.GetRequiredService<LyricsContext>(),
            sp.GetRequiredService<ILyricsCacheStore>(), sp.GetRequiredService<LyricsProviderChain>()));
        services.AddSingleton(sp => new LyricsWorkflow(sp.GetRequiredService<LyricsLoader>(),
            sp.GetRequiredService<ILyricsChoiceStore>(), sp.GetRequiredService<PlaybackContext>(), sp.GetRequiredService<WorkflowTasks>()));
        services.AddSingleton(sp => new CoverWorkflow(sp.GetRequiredService<WorkflowTasks>()));
        services.AddSingleton<IPlaybackGateway>(sp => gateway ?? new SmtcPlaybackGateway(sp.GetRequiredService<IStateDispatcher>()));
        services.AddSingleton(sp => new PlaybackWorkflow(sp.GetRequiredService<IStateDispatcher>(), sp.GetRequiredService<IPlaybackGateway>(),
            sp.GetRequiredService<PlaybackContext>(), sp.GetRequiredService<LyricsWorkflow>(),
            sp.GetRequiredService<CoverWorkflow>(), sp.GetRequiredService<WorkflowTasks>()));
        services.AddSingleton(sp => new NowPlayingProjection(sp.GetRequiredService<PlaybackContext>(),
            sp.GetRequiredService<LyricsContext>(), sp.GetRequiredService<ILyricsOffsetStore>(), sp.GetRequiredService<CoverWorkflow>()));
        services.AddSingleton<INowPlayingQueries>(sp => new NowPlayingQueries(sp.GetRequiredService<NowPlayingProjection>(),
            sp.GetRequiredService<PlaybackContext>(), sp.GetRequiredService<LyricsLoader>(), sp.GetRequiredService<ILyricsOffsetStore>(),
            sp.GetRequiredService<WorkflowTasks>()));
        services.AddSingleton(sp => new PlaybackRuntime(sp.GetRequiredService<PlaybackWorkflow>(), sp.GetRequiredService<IPlaybackGateway>(),
            sp.GetRequiredService<IStateDispatcher>(), sp.GetRequiredService<WorkflowTasks>(), sp.GetRequiredService<NowPlayingProjection>(),
            sp.GetRequiredService<CoverWorkflow>(), sp.GetRequiredService<TimeProvider>()));
        services.AddCommand<PreviousTrack, bool, PreviousTrackHandler>(sp => new(sp.GetRequiredService<PlaybackWorkflow>()));
        services.AddCommand<TogglePlayback, bool, TogglePlaybackHandler>(sp => new(sp.GetRequiredService<PlaybackWorkflow>()));
        services.AddCommand<NextTrack, bool, NextTrackHandler>(sp => new(sp.GetRequiredService<PlaybackWorkflow>()));
        services.AddCommand<SelectPlaybackSource, bool, SelectPlaybackSourceHandler>(sp => new(sp.GetRequiredService<IPlaybackGateway>()));
        services.AddCommand<SetMediaControls, bool, SetMediaControlsHandler>(sp => new(sp.GetRequiredService<PlaybackWorkflow>()));
        services.AddCommand<ApplyLyricsCandidate, bool, ApplyLyricsCandidateHandler>(sp => new(sp.GetRequiredService<PlaybackContext>(), sp.GetRequiredService<LyricsWorkflow>()));
        services.AddCommand<SetLyricOffset, bool, SetLyricOffsetHandler>(sp => new(sp.GetRequiredService<PlaybackContext>(), sp.GetRequiredService<ILyricsOffsetStore>()));
        services.AddCommand<RestoreAutomaticLyrics, bool, RestoreAutomaticLyricsHandler>(sp => new(sp.GetRequiredService<PlaybackContext>(), sp.GetRequiredService<LyricsWorkflow>()));
        services.AddCommand<ClearLyricsData, bool, ClearLyricsDataHandler>(sp => new(sp.GetRequiredService<LyricsWorkflow>()));
        services.AddCommand<RejectCover, bool, RejectCoverHandler>(sp => new(sp.GetRequiredService<CoverWorkflow>()));
        services.AddSingleton<ICommandBus>(sp => new CommandBus(sp, sp.GetServices<CommandRegistration>(),
            sp.GetRequiredService<IStateDispatcher>(), sp.GetRequiredService<NowPlayingProjection>(), sp.GetRequiredService<WorkflowTasks>()));
    }

    private sealed class HostedPlayback(PlaybackRuntime runtime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => runtime.StartAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => runtime.StopAsync(cancellationToken);
    }
    // WPF 拥有消息循环，启动和退出由 App 显式衔接；Host 不接管控制台生命周期。
    private sealed class WpfHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
