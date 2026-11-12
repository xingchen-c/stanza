using System.Windows.Threading;
using Windows.Media.Control;
using Stanza.Models;
using Stanza.Services.Playback;

namespace Stanza.Services;

/// <summary>一次选中的 SMTC 会话及其播放源策略。</summary>
internal readonly record struct SmtcSessionSelection(
    GlobalSystemMediaTransportControlsSession Session,
    IPlaybackSourcePolicy Policy,
    IPlaybackSourceAdapter Adapter,
    string AppUserModelId);

/// <summary>
///     SMTC 会话选择器：负责管理器初始化、会话发现、筛选和选择。
///     对上层只发布当前选中的会话及其播放源策略。
/// </summary>
internal sealed class SmtcSessionSelector(Dispatcher dispatcher) : IDisposable
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _selectedSessionId = string.Empty;
    private bool _lastSelectionWasEmpty;

    /// <summary>选中的会话变化；null 表示当前模式下没有可用会话。</summary>
    public event Action<SmtcSessionSelection?>? SelectionChanged;

    /// <summary>SMTC 管理器初始化失败时的用户可读消息。</summary>
    public event Action<string>? InitializationFailed;

    public string PlaybackSourceMode { get; private set; } = PlaybackSourceIds.Auto;

    public GlobalSystemMediaTransportControlsSession? CurrentSession => _session;

    /// <summary>初始化 SMTC 管理器、启动会话刷新并接入当前会话。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();
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
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;

        if (_manager is not null)
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;

        DetachSession();
        _manager = null;
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e) => RefreshSelection();

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        dispatcher.BeginInvoke(RefreshSelection);
    }

    private void RefreshSelection()
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
            _selectedSessionId = string.Empty;
            DetachSession();
            if (!_lastSelectionWasEmpty)
            {
                _lastSelectionWasEmpty = true;
                SelectionChanged?.Invoke(null);
            }

            return;
        }

        var selected = pairs[selectedIndex];
        if (_selectedSessionId == selected.Descriptor.SessionId)
            return;

        _lastSelectionWasEmpty = false;
        _selectedSessionId = selected.Descriptor.SessionId;
        _session = selected.Session;

        var policy = PlaybackSourceRegistry.ResolveBySession(
            selected.Descriptor.SourceAppUserModelId);
        var adapter = PlaybackSourceRegistry.ResolveAdapterBySession(
            selected.Descriptor.SourceAppUserModelId);
        SelectionChanged?.Invoke(new SmtcSessionSelection(
            selected.Session,
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
                session.GetPlaybackInfo().PlaybackStatus);
        }
        catch
        {
            return PlaybackState.Unknown;
        }
    }

    private void DetachSession()
    {
        _session = null;
    }
}
