using Windows.Media.Control;

namespace Stanza.Infrastructure.Playback;

/// <summary>包装一个实际 SMTC 对象；网关切换选择时释放包装并退订原始事件。</summary>
internal sealed class SmtcPlaybackSession : IPlaybackSession, IDisposable
{
    private readonly GlobalSystemMediaTransportControlsSession _session;
    public SmtcPlaybackSession(GlobalSystemMediaTransportControlsSession session)
    {
        _session = session;
        session.MediaPropertiesChanged += OnMedia;
        session.TimelinePropertiesChanged += OnTimeline;
        session.PlaybackInfoChanged += OnPlayback;
    }
    public event Action<IPlaybackSession>? MediaChanged;
    public event Action<IPlaybackSession>? TimelineChanged;
    public event Action<IPlaybackSession>? PlaybackChanged;
    private void OnMedia(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => MediaChanged?.Invoke(this);
    private void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => TimelineChanged?.Invoke(this);
    private void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => PlaybackChanged?.Invoke(this);
    public RawPlaybackStatus GetStatus() => (RawPlaybackStatus)_session.GetPlaybackInfo().PlaybackStatus;
    public PlaybackCapabilities GetCapabilities()
    {
        var controls = _session.GetPlaybackInfo().Controls;
        return new(controls.IsPreviousEnabled,
            controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
            controls.IsNextEnabled, controls.IsPlaybackPositionEnabled, true);
    }
    public PlaybackTimeline GetTimeline()
    {
        var timeline = _session.GetTimelineProperties();
        return new(timeline.Position, timeline.EndTime, timeline.LastUpdatedTime);
    }
    public async Task<PlaybackMedia> ReadMediaAsync(CancellationToken cancellationToken = default)
    {
        var props = await _session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
        // 委托捕获本次属性快照；晚到封面需要工作流重新读媒体，不能期待此快照自动更新。
        return new(props.Title ?? string.Empty, props.Artist ?? string.Empty, props.AlbumTitle ?? string.Empty, async token =>
        {
            if (props.Thumbnail is null) return null;
            using var stream = await props.Thumbnail.OpenReadAsync().AsTask(token);
            using var output = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(output, token);
            return output.ToArray();
        });
    }
    public async Task PreviousAsync() => _ = await _session.TrySkipPreviousAsync();
    public async Task TogglePlayPauseAsync() => _ = await _session.TryTogglePlayPauseAsync();
    public async Task NextAsync() => _ = await _session.TrySkipNextAsync();
    public void Dispose()
    {
        _session.MediaPropertiesChanged -= OnMedia;
        _session.TimelinePropertiesChanged -= OnTimeline;
        _session.PlaybackInfoChanged -= OnPlayback;
    }
}
