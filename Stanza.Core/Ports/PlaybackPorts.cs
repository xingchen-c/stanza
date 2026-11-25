namespace Stanza.Core.Ports;

// 平台适配器按 WinRT 枚举值转换；调整成员顺序时必须同步修改该转换。
internal enum RawPlaybackStatus { Closed, Opened, Changing, Stopped, Playing, Paused }

/// <summary>Position 是 LastUpdatedTime 时刻的快照，不能直接当作当前实时位置。</summary>
internal sealed record PlaybackTimeline(TimeSpan Position, TimeSpan EndTime, DateTimeOffset LastUpdatedTime);

/// <summary>一次媒体读取的结果；封面延迟读取，避免未启用媒体区时仍访问图片流。</summary>
internal sealed record PlaybackMedia(
    string Title, string Artist, string AlbumTitle,
    Func<CancellationToken, Task<byte[]?>> ReadCoverAsync);

/// <summary>隔离原始平台会话；通知保留发送者，供调度后排除旧会话的排队事件。</summary>
internal interface IPlaybackSession
{
    event Action<IPlaybackSession>? MediaChanged;
    event Action<IPlaybackSession>? TimelineChanged;
    event Action<IPlaybackSession>? PlaybackChanged;
    RawPlaybackStatus GetStatus();
    PlaybackCapabilities GetCapabilities();
    PlaybackTimeline GetTimeline();
    Task<PlaybackMedia> ReadMediaAsync(CancellationToken cancellationToken = default);
    Task PreviousAsync();
    Task TogglePlayPauseAsync();
    Task NextAsync();
}

internal readonly record struct PlaybackSelection(
    IPlaybackSession Session, IPlaybackSourcePolicy Policy,
    IPlaybackSourceAdapter Adapter, string AppUserModelId);

/// <summary>负责会话发现、选择及所创建会话包装的释放；业务轮询由运行时驱动。</summary>
internal interface IPlaybackGateway : IDisposable
{
    event Action<PlaybackSelection?>? SelectionChanged;
    event Action<string>? InitializationFailed;
    string PlaybackSourceMode { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    void RefreshSelection();
    void SetPlaybackSource(string? mode);
}

/// <summary>
/// 将状态操作调度到同一执行上下文。异步等待期间允许其他操作进入，回包仍需校验请求身份。
/// </summary>
internal interface IStateDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default);
    Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);
}

/// <summary>只持久化设置值；Windows 自启由独立端口执行。</summary>
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface IAutoStart
{
    void Apply(bool enabled);
}
