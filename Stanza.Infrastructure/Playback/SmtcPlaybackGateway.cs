
using Windows.Media.Control;
using Stanza.Core.Models;
using Stanza.Core.Playback;

namespace Stanza.Infrastructure.Playback;

/// <summary>
///     SMTC 会话选择器：负责管理器初始化、会话发现、筛选和选择。
///     对上层只发布当前选中的会话及其播放源策略。
/// </summary>
internal sealed class SmtcPlaybackGateway(IStateDispatcher dispatcher) : IPlaybackGateway
{
    private SmtcPlaybackSession? _selected;
    private bool _disposed;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _lastSelectionWasEmpty;

    /// <summary>选中的会话变化；null 表示当前模式下没有可用会话。</summary>
    public event Action<PlaybackSelection?>? SelectionChanged;

    /// <summary>SMTC 管理器初始化失败时的用户可读消息。</summary>
    public event Action<string>? InitializationFailed;

    public string PlaybackSourceMode { get; private set; } = PlaybackSourceIds.Auto;

    public GlobalSystemMediaTransportControlsSession? CurrentSession => _session;

    /// <summary>初始化 SMTC 管理器、启动会话刷新并接入当前会话。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);
            if (_disposed) return;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            RefreshSelection();
        }
        catch (Exception ex)
        {
            AppLog.Error($"SMTC 初始化失败: {ex.Message}");
            InitializationFailed?.Invoke($"SMTC 初始化失败：{ex.Message}");
        }
    }

    /// <summary>更新播放来源模式并立即重新选择会话。</summary>
    public void SetPlaybackSource(string? mode)
    {
        PlaybackSourceMode = string.IsNullOrWhiteSpace(mode)
            ? PlaybackSourceIds.Auto
            : mode;
        _lastSelectionWasEmpty = false;
        RefreshSelection();
    }

    public void Dispose()
    {
        _disposed = true;

        if (_manager is not null)
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;

        DetachSession();
        _manager = null;
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        dispatcher.Post(() => { if (!_disposed) RefreshSelection(); });
    }

    public void RefreshSelection()
    {
        if (_disposed) return;
        try { RefreshCurrentSelection(); }
        catch (Exception ex) { AppLog.Debug($"SMTC 会话刷新失败: {ex.Message}"); }
    }

    private void RefreshCurrentSelection()
    {
        var sessions = _manager?.GetSessions() ?? [];
        var pairs = sessions
            .Select((session, index) => (
                Session: session,
                Descriptor: CreateDescriptor(session, index)))
            .ToList();

        var selectedIndex = PlaybackSourceSelector.SelectIndex(
            [.. pairs.Select(x => x.Descriptor)],
            PlaybackSourceMode,
            GetCurrentSessionId(pairs));

        if (selectedIndex < 0)
        {
            DetachSession();
            if (!_lastSelectionWasEmpty)
            {
                _lastSelectionWasEmpty = true;
                SelectionChanged?.Invoke(null);
            }

            return;
        }

        var selected = pairs[selectedIndex];
        // 同一 AUMID 可能已换成另一个会话，必须比较实际对象，不能只比较播放器名称。
        if (ReferenceEquals(_session, selected.Session))
            return;

        _lastSelectionWasEmpty = false;
        _selected?.Dispose();
        _session = selected.Session;
        _selected = new SmtcPlaybackSession(selected.Session);

        var policy = PlaybackSourceRegistry.ResolveBySession(
            selected.Descriptor.SourceAppUserModelId);
        var adapter = PlaybackSourceRegistry.ResolveAdapterBySession(
            selected.Descriptor.SourceAppUserModelId);
        SelectionChanged?.Invoke(new PlaybackSelection(
            _selected,
            policy,
            adapter,
            selected.Descriptor.SourceAppUserModelId));
    }

    private PlaybackSessionDescriptor CreateDescriptor(
        GlobalSystemMediaTransportControlsSession session,
        int index)
    {
        var aumid = GetSourceAppUserModelId(session);
        var state = GetPlaybackState(session);
        var currentSession = _manager?.GetCurrentSession();

        return new PlaybackSessionDescriptor(
            SessionId: $"{aumid}:{index}",
            SourceAppUserModelId: aumid,
            PlaybackState: state,
            IsCurrentSession: ReferenceEquals(session, currentSession));
    }

    private string? GetCurrentSessionId(
        IReadOnlyList<(
            GlobalSystemMediaTransportControlsSession Session,
            PlaybackSessionDescriptor Descriptor)> sessions)
    {
        var currentSession = _manager?.GetCurrentSession();
        if (currentSession is null)
            return null;

        foreach (var item in sessions)
            if (ReferenceEquals(item.Session, currentSession))
                return item.Descriptor.SessionId;

        return null;
    }

    private static string GetSourceAppUserModelId(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.SourceAppUserModelId ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static PlaybackState GetPlaybackState(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return PlaybackStateMapper.Map(
                (RawPlaybackStatus)session.GetPlaybackInfo().PlaybackStatus);
        }
        catch
        {
            return PlaybackState.Unknown;
        }
    }

    private void DetachSession()
    {
        _selected?.Dispose();
        _selected = null;
        _session = null;
    }
}
