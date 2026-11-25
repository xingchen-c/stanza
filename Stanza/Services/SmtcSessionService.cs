using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Stanza.Models;
using Stanza.Services.Lyrics;
using Stanza.Services.Playback;

namespace Stanza.Services;

/// <summary>
///     SMTC 选中会话协调器：接收 SmtcSessionSelector 的选择结果，负责会话事件、歌词加载、
///     切歌状态机、位置采样、封面管线和播放控制。
///     与 UI 解耦：通过事件向外通知状态变化，MainWindow 只订阅事件做渲染。
/// </summary>
public sealed class SmtcSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly SmtcSessionSelector _selector;
    private readonly Dispatcher _dispatcher;

    // 当前的key
    private string _currentKey = string.Empty;
    private readonly MediaRequestIdentity _requests = new();
    private bool _lyricsRequested;
    private bool _disposed;

    private readonly TrackSwitchStateMachine _switchFsm = new();
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastPlaybackStatus;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastEventPlaybackStatus;
    private NowPlaying? _nowPlaying;
    private long _lastSwitchStartUtcMs;
    private double _lastEndTimeMs = -1;
    private readonly TrackDurationEvidence _durationEvidence = new();
    private int _lastTickPositionMs = -1;
    private int _lastTimelineEventLogMs;

    /// <summary>会话接入/断开（参数：是否已连接）</summary>
    public event Action<bool>? SessionChanged;

    /// <summary>需要显示的状态文本（"正在获取歌词…" / "未找到歌词" / 空串 = 恢复正常）</summary>
    public event Action<string>? StatusChanged;

    /// <summary>切歌开始：UI 应清掉当前渲染状态，等待新歌词</summary>
    public event Action? TrackSwitchStarted;

    /// <summary>某首歌的歌词加载完成（key + 结果）</summary>
    public event Action<string, LyricsLoadResult>? TrackLyricsChanged;

    /// <summary>播放状态变化（图标/显隐刷新用）</summary>
    public event Action<PlaybackState>? PlaybackStatusChanged;

    /// <summary>当前会话支持的播放能力变化（按钮/封面显隐用）</summary>
    public event Action<PlaybackCapabilities>? PlaybackCapabilitiesChanged;

    /// <summary>封面就绪/变化（key + 位图；null = 无封面或加载失败）</summary>
    public event Action<string, BitmapSource?>? CoverChanged
    {
        add => _coverPipeline.CoverChanged += value;
        remove => _coverPipeline.CoverChanged -= value;
    }

    /// <summary>当前歌曲标题（悬停面板显示用）</summary>
    public string CurrentTitle => _nowPlaying?.Title ?? string.Empty;

    /// <summary>当前歌曲歌手（悬停面板显示用）</summary>
    public string CurrentArtist => _nowPlaying?.Artist ?? string.Empty;

    /// <summary>当前播放状态（UI 显隐/图标使用）</summary>
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Unknown;

    /// <summary>当前会话运行时播放能力</summary>
    public PlaybackCapabilities Capabilities { get; private set; } =
        new(false, false, false, false, false);

    /// <summary>当前生效的播放源策略 Key（QQ / NetEase / Generic；诊断和 UI 展示用）</summary>
    public string CurrentPlaybackSourcePolicyKey => _sourcePolicy.Key;

    /// <summary>是否正在等待新歌歌词</summary>
    public bool IsSwitchingTrack => _switchFsm.IsSwitching;

    /// <summary>歌词库（UI 渲染读取行数据）</summary>
    public LyricsService Lyrics => _lyricsCoordinator.Lyrics;

    public int CurrentLyricOffsetMs => _lyricsOffsetStore.Get(_currentKey);

    private static readonly IPlaybackSourceAdapter DefaultAdapter =
        PlaybackSourceRegistry.ResolveAdapterByKey(PlaybackSourceIds.AutoFallback);

    private IPlaybackSourcePolicy _sourcePolicy = PlaybackSourceRegistry.ResolveByKey(PlaybackSourceIds.AutoFallback);
    private IPlaybackSourceAdapter _sourceAdapter = DefaultAdapter;
    private readonly PlaybackCoverPipeline _coverPipeline = new(DefaultAdapter);

    private PlaybackPositionTracker _positionTracker = new(PlaybackTimingPolicy.Default, PlaybackSwitchPolicy.Default);

    private string _sourceAppUserModelId = "?";
    private string? _lastNoSessionStatus;
    private int _coverRetryGeneration;
    private readonly LyricsCoordinator _lyricsCoordinator;
    private readonly ILyricsOffsetStore _lyricsOffsetStore;

    /// <summary>创建播放协调器，创建会话选择器并订阅其选择结果。</summary>
    internal SmtcSessionService(
        Dispatcher dispatcher,
        ILyricsCacheStore lyricsCacheStore,
        ILyricsChoiceStore lyricsChoiceStore,
        ILyricsOffsetStore lyricsOffsetStore)
    {
        _dispatcher = dispatcher;
        _lyricsOffsetStore = lyricsOffsetStore;
        _lyricsCoordinator = new LyricsCoordinator(
            lyricsCacheStore,
            lyricsChoiceStore,
            () => _currentKey,
            new QqMusicLyricsProvider(),
            new NeteaseLyricsProvider(),
            new LrclibLyricsProvider());
        _lyricsCoordinator.ResultApplied += OnLyricsResultApplied;
        _lyricsCoordinator.StatusChanged += text => StatusChanged?.Invoke(text);
        _selector = new SmtcSessionSelector(dispatcher);
        _selector.SelectionChanged += OnSessionSelectionChanged;
        _selector.InitializationFailed += message => StatusChanged?.Invoke(message);
    }

    /// <summary>初始化 SMTC 管理器并接入当前会话</summary>
    public Task InitializeAsync() => _selector.InitializeAsync();

    /// <summary>更新播放源模式并立即重新选择会话。</summary>
    public void SetPlaybackSource(string? mode) => _selector.SetPlaybackSource(mode);

    private void OnSessionSelectionChanged(SmtcSessionSelection? selection)
    {
        if (_disposed)
            return;
        if (selection is not { } selected)
        {
            if (_session is not null)
                UpdateSession(null);

            var waitingText = BuildWaitingText(_selector.PlaybackSourceMode);
            if (string.Equals(waitingText, _lastNoSessionStatus, StringComparison.Ordinal))
                return;

            _lastNoSessionStatus = waitingText;
            StatusChanged?.Invoke(waitingText);
            return;
        }

        _lastNoSessionStatus = null;
        _sourcePolicy = selected.Policy;
        _sourceAdapter = selected.Adapter;
        _sourceAppUserModelId = selected.AppUserModelId;
        _coverPipeline.SetAdapter(_sourceAdapter);
        AppLog.Info(
            $"播放源策略: 模式={_selector.PlaybackSourceMode} 会话来源={_sourceAppUserModelId} " +
            $"策略={_sourcePolicy.Key}（{_sourcePolicy.DisplayName}）");

        _positionTracker = new PlaybackPositionTracker(
            _sourcePolicy.Timing,
            _sourcePolicy.Switch);
        UpdateSession(selected.Session);
    }

    private static string BuildWaitingText(string mode)
    {
        return mode switch
        {
            PlaybackSourceIds.QqMusic => "等待 QQ 音乐播放…",
            PlaybackSourceIds.NetEase => "等待网易云音乐播放…",
            _ => "等待播放音乐…"
        };
    }

    internal PlaybackSample SamplePlayback(long nowUtcMs)
    {
        var session = _session;
        if (session is null)
            return new PlaybackSample(0, 0, PlaybackState.Unknown, false, "无会话");

        var timeline = GetTimeline();
        if (timeline is null)
            return new PlaybackSample(0, 0, PlaybackState.Unknown, false, "时间线不可用");

        PlaybackState state;
        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            if (ApplyPlaybackState(status, nowUtcMs))
                PlaybackStatusChanged?.Invoke(PlaybackState);
            state = PlaybackState;
        }
        catch
        {
            state = PlaybackState.Unknown;
        }

        var rawPositionMs = (int)timeline.Position.TotalMilliseconds;
        var result = _positionTracker.Sample(
            rawPositionMs,
            timeline.LastUpdatedTime.ToUnixTimeMilliseconds(),
            state == PlaybackState.Playing,
            nowUtcMs);

        if (result.ShouldRender)
            NotifyPlaybackPosition(rawPositionMs);

        return new PlaybackSample(
            result.PositionMs,
            rawPositionMs,
            state,
            result.ShouldRender,
            result.SkipReason);
    }


    /// <summary>释放：退订 SMTC 事件、取消"未找到歌词"恢复调度（应用退出时调用）</summary>
    public void Dispose()
    {
        _disposed = true;
        _requests.ChangeSession(null);
        Lyrics.InvalidateInFlight();
        _lyricsCoordinator.CancelRecovery();
        _selector.Dispose();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = null;
    }

    /// <summary>是否处于"正在播放"（含切歌中的 Changing）；查询失败按播放处理，走原有等待逻辑</summary>
    private static bool IsActuallyPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var status = session.GetPlaybackInfo().PlaybackStatus;
            return status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing;
        }
        catch
        {
            return true;
        }
    }

    private void UpdateSession(GlobalSystemMediaTransportControlsSession? session)
    {
        _lyricsCoordinator.CancelRecovery(); // 会话变化/断开：旧歌的恢复调度立即作废

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = session;
        _requests.ChangeSession(session);
        _currentKey = string.Empty;
        _nowPlaying = null;
        _lyricsRequested = false;
        Lyrics.InvalidateInFlight();
        Lyrics.Clear();
        PlaybackState = PlaybackState.Unknown;
        SetPlaybackCapabilities(new PlaybackCapabilities(false, false, false, false, false));
        _lastTickPositionMs = -1;
        _lastEndTimeMs = -1;
        _durationEvidence.Reset();
        _switchFsm.Reset();
        _lastPlaybackStatus = null;
        _lastEventPlaybackStatus = null;
        _lastSwitchStartUtcMs = 0;
        _positionTracker.Reset();

        if (_session is null)
        {
            AppLog.Info("SMTC 会话断开");
            _coverPipeline.Clear();
            SetPlaybackCapabilities(new PlaybackCapabilities(false, false, false, false, false));
            SessionChanged?.Invoke(false);
            StatusChanged?.Invoke("等待播放音乐…");
            return;
        }

        AppLog.Info($"SMTC 会话接入: 来源={_sourceAppUserModelId}");
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        UpdatePlaybackCapabilities(_session);

        // 缓存当前播放状态，供 PlaybackInfoChanged 去重比较
        try
        {
            var status = _session.GetPlaybackInfo().PlaybackStatus;
            ApplyPlaybackState(status, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _lastEventPlaybackStatus = status;
        }
        catch
        {
            // 某些播放器不提供播放信息，忽略
        }

        // 缓存当前歌曲时长，供"时长变化 = 切歌"早期信号比较
        try
        {
            var endMs = _session.GetTimelineProperties().EndTime.TotalMilliseconds;
            if (endMs > 0)
                _lastEndTimeMs = endMs;
        }
        catch
        {
            // 某些播放器不提供时长，忽略
        }

        SessionChanged?.Invoke(true);
        PlaybackStatusChanged?.Invoke(PlaybackState);
        _ = RefreshTrackAsync();
    }

    /// <summary>切歌时读取歌曲信息，若和上次不同则重新搜索歌词</summary>
    public async Task RefreshTrackAsync()
    {
        var session = _session;
        if (session is null || _disposed)
            return;

        var request = _requests.BeginMetadata(session);
        var policy = _sourcePolicy;
        int? lyricsGeneration = null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (!_requests.IsCurrent(request))
                return;
            var metadata = policy.NormalizeMetadata(new PlaybackMetadata(
                props.Title ?? string.Empty,
                props.Artist ?? string.Empty,
                props.AlbumTitle ?? string.Empty,
                null));
            metadata = metadata with { DurationMs = ReadDurationMs(metadata.Title, metadata.Artist) };
            var title = metadata.Title;
            var artist = metadata.Artist;
            var lyricsQuery = new LyricsQuery(title, artist, metadata.DurationMs, metadata.Album,
                PreferredLyricsSource(_sourcePolicy.Key, _sourceAppUserModelId));
            AppLog.Debug($"媒体属性: 标题={title} 歌手={artist} 封面引用={props.Thumbnail is not null}");
            // 切歌瞬间元数据可能短暂为空：保留上一首的显示信息，避免悬停面板闪空歌名
            if (!string.IsNullOrWhiteSpace(title))
            {
                _nowPlaying = new NowPlaying(
                    TrackKey.Of(title, artist),
                    title,
                    artist,
                    metadata.Album,
                    metadata.DurationMs,
                    PlaybackState,
                    _sourcePolicy.Key,
                    _sourceAppUserModelId);
            }

            var playing = IsActuallyPlaying(session);
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (_requests.ConfirmTrack(lyricsQuery))
                {
                    _lyricsCoordinator.CancelRecovery();
                    if (playing && !IsSwitchingTrack)
                        BeginTrackSwitch("元数据变化");
                    _currentKey = lyricsQuery.Key;
                    _lyricsRequested = false;
                    Lyrics.InvalidateInFlight();
                    Lyrics.Clear();
                    _positionTracker.OnTrackConfirmed();
                }
            }
            else
            {
                _requests.SetMetadataUnavailable();
            }

            // 未播放（暂停/停止/未开始）：待机状态——显示"歌名 - 歌手"或"等待播放音乐…"，
            // 不进入切歌等待/歌词加载流程（避免误导性的"正在获取歌词…"）。
            // 已有歌词（播放中暂停）保持显示，不覆盖。
            if (!playing)
            {
                _switchFsm.Cancel();
                var idleKey = string.IsNullOrWhiteSpace(title) ? string.Empty : TrackKey.Of(title, artist);
                _ = RefreshCoverWithRetryAsync(session, props, idleKey);
                if (Lyrics.Lines.Count == 0)
                    StatusChanged?.Invoke(string.IsNullOrWhiteSpace(title)
                        ? "等待播放音乐…"
                        : string.IsNullOrWhiteSpace(artist)
                            ? title
                            : $"{title} - {artist}");
                return;
            }

            // 切歌瞬间 SMTC 元数据可能短暂为空：不能把空值当成"未知歌曲"去搜索，
            // 否则会污染当前歌曲 key，并让轮询误以为"新歌已确认"而提前终止。
            // 这里只等待，不更新任何状态，由轮询/后续事件重读。
            if (string.IsNullOrWhiteSpace(title))
            {
                AppLog.Debug("元数据未就绪（空标题），等待轮询重读");
                if (!IsSwitchingTrack)
                    BeginTrackSwitch("元数据未就绪"); // 顺带启动元数据轮询，等真实歌名出现
                return;
            }

            var key = TrackKey.Of(title, artist);
            _ = RefreshCoverWithRetryAsync(session, props, key);
            if (_lyricsRequested)
                return;

            // 暂停时已确认的新歌在恢复播放后仍需加载，不能只按 key 相等提前返回。
            if (!IsSwitchingTrack)
                BeginTrackSwitch("开始加载");
            _positionTracker.OnTrackConfirmed();
            _lyricsRequested = true;
            var context = _requests.CreateContext()!;
            var generation = Lyrics.Generation;
            lyricsGeneration = generation;
            _switchFsm.MarkLoading();
            AppLog.Info($"开始加载歌词: {title} / {artist}");

            var result = await _lyricsCoordinator.LoadAsync(lyricsQuery);

            // 等待期间可能已经切到下一首歌，旧结果直接丢弃
            if (!_requests.IsTrackCurrent(context) || !Lyrics.IsCurrentGeneration(generation))
                return;

            AppLog.Info($"歌词加载完成: {result}");
            _switchFsm.Complete(Environment.TickCount);

            // 歌词已就绪，但 SMTC 的位置快照可能还是切歌前的旧值（新快照未到）：
            // 此时先保留"正在获取歌词…"占位，避免加载完成后出现空白；
            // 新快照到达后 ticker 会立即按真实位置渲染。
            var snapshotReady = true;
            if (result == LyricsLoadResult.Loaded && _lastSwitchStartUtcMs > 0)
                try
                {
                    var timeline = session.GetTimelineProperties();
                    snapshotReady = timeline.LastUpdatedTime.ToUnixTimeMilliseconds() >= _lastSwitchStartUtcMs;
                }
                catch
                {
                    // 读取失败按可渲染处理
                }

            _lyricsCoordinator.ApplyResult(
                key,
                result,
                showNotFound: snapshotReady && result != LyricsLoadResult.NotFound);
            if (!snapshotReady)
                AppLog.Debug($"加载完成但时间线快照未就绪，保留占位等待新快照: key={key}");

            // 偶发：切歌瞬间搜索失败 / QQ 接口限流。NotFound 不缓存，延迟重试一次，
            // 避免"未找到歌词"被定死整首歌（重试期间已有占位文本，无闪烁）。
            if (result is LyricsLoadResult.NotFound or LyricsLoadResult.Ambiguous)
                _lyricsCoordinator.ScheduleRecovery(
                    key,
                    lyricsQuery,
                    token => ReadCurrentQueryForRecoveryAsync(key, token));
        }
        catch (Exception ex)
        {
            if (!_requests.IsCurrent(request) ||
                (lyricsGeneration is { } generation && !Lyrics.IsCurrentGeneration(generation)))
                return;
            AppLog.Error($"读取媒体信息失败: {ex.Message}");
            StatusChanged?.Invoke($"读取媒体信息失败：{ex.Message}");
        }
    }

    internal static string? PreferredLyricsSource(string policyKey, string sourceAppUserModelId)
    {
        if (policyKey is PlaybackSourceIds.QqMusic or PlaybackSourceIds.NetEase)
            return policyKey;
        // QQ 的另一种实机 AUMID 尚未套用专用播放阈值，但仍可提供歌词来源偏好。
        return string.Equals(sourceAppUserModelId, "Tencent.QQMusic", StringComparison.OrdinalIgnoreCase)
            ? "QQ" : null;
    }

    /// <summary>读取当前歌曲时长（搜索匹配辅助；切歌瞬间 EndTime 可能还是旧值）</summary>
    private int? ReadDurationMs(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        try
        {
            var timeline = _session?.GetTimelineProperties();
            if (timeline is not null)
                return _durationEvidence.Read(TrackKey.Of(title, artist),
                    timeline.EndTime.TotalMilliseconds is > 0 and <= int.MaxValue
                        ? (int)timeline.EndTime.TotalMilliseconds : null,
                    timeline.LastUpdatedTime.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch
        {
            // 读不到时长就按不带时长搜索
        }

        return null;
    }

    /// <summary>
    ///     封面字段常在切歌后短暂缺失。首次读取失败后短时间内主动重读，
    ///     不依赖播放器是否继续推送 MediaPropertiesChanged。
    /// </summary>
    private async Task RefreshCoverWithRetryAsync(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionMediaProperties props,
        string key)
    {
        var generation = Interlocked.Increment(ref _coverRetryGeneration);
        await _coverPipeline.RefreshAsync(
            props,
            key,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (string.IsNullOrEmpty(key))
            return;

        foreach (var delayMs in new[] { 150, 350, 800, 1500 })
        {
            await Task.Delay(delayMs);
            if (generation != _coverRetryGeneration || !ReferenceEquals(session, _session) || _disposed)
                return;

            try
            {
                props = await session.TryGetMediaPropertiesAsync();
            }
            catch
            {
                return;
            }

            if (generation != _coverRetryGeneration || !ReferenceEquals(session, _session) || _disposed)
                return;

            await _coverPipeline.RefreshAsync(
                props,
                key,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>捕获打开选择器时的曲目身份，空标题或无会话时不可选择。</summary>
    public LyricsSelectionContext? CreateLyricsSelectionContext() => _requests.CreateContext();

    public bool IsLyricsSelectionCurrent(LyricsSelectionContext context)
        => !_disposed && _requests.IsSelectionCurrent(context);

    /// <summary>只搜索打开选择器时捕获的曲目，过期请求不返回可用列表。</summary>
    public async Task<IReadOnlyList<LyricCandidate>> GetLyricCandidatesAsync(
        LyricsSelectionContext context, CancellationToken cancellationToken = default)
    {
        if (!IsLyricsSelectionCurrent(context))
            return [];
        var candidates = await _lyricsCoordinator.GetCandidatesAsync(context.Query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return IsLyricsSelectionCurrent(context) ? candidates : [];
    }

    /// <summary>应用用户选中的候选：取词 + 显示 + 记忆，绝不回写曲目身份。</summary>
    public async Task<bool> ApplyLyricCandidateAsync(
        LyricsSelectionContext context, LyricCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (!IsLyricsSelectionCurrent(context))
            return false;
        var applied = await _lyricsCoordinator.ApplyCandidateAsync(context.Key, candidate, cancellationToken);
        if (!applied || !IsLyricsSelectionCurrent(context))
            return false;
        _lyricsRequested = true;
        return true;
    }

    public int GetLyricOffset(LyricsSelectionContext context) => _lyricsOffsetStore.Get(context.Key);

    public bool SetLyricOffset(LyricsSelectionContext context, int offsetMs)
    {
        if (!IsLyricsSelectionCurrent(context) ||
            offsetMs is < -ILyricsOffsetStore.MaximumOffsetMs or > ILyricsOffsetStore.MaximumOffsetMs)
            return false;
        return _lyricsOffsetStore.Set(context.Key, offsetMs);
    }

    public async Task<bool> RestoreAutomaticLyricsAsync(
        LyricsSelectionContext context, CancellationToken cancellationToken = default)
    {
        if (!IsLyricsSelectionCurrent(context))
            return false;
        var restored = await _lyricsCoordinator.RestoreAutomaticAsync(context.Query, cancellationToken);
        if (!restored || !IsLyricsSelectionCurrent(context))
            return false;
        _lyricsRequested = true;
        return true;
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _session))
                _ = RefreshTrackAsync();
        });
    }

    /// <summary>媒体区开关：关闭时不再加载封面</summary>
    public void SetMediaControlsEnabled(bool enabled)
    {
        _coverPipeline.SetEnabled(enabled);
    }

    /// <summary>清除歌词内容缓存与用户手动选择记忆。</summary>
    public void ClearLyricsData()
    {
        _lyricsCoordinator.ClearAll();
    }

    private void OnLyricsResultApplied(string key, LyricsLoadResult result)
    {
        _switchFsm.Complete(Environment.TickCount);
        TrackLyricsChanged?.Invoke(key, result);
    }

    private async Task<RecoveryProbe> ReadCurrentQueryForRecoveryAsync(
        string expectedKey,
        CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null)
            return RecoveryProbe.Abort();

        var request = _requests.Capture(session);
        var revision = _requests.Revision;
        if (_currentKey != expectedKey)
            return RecoveryProbe.Abort();
        if (!IsActuallyPlaying(session))
            return RecoveryProbe.Retry();

        var policy = _sourcePolicy;
        var props = await session.TryGetMediaPropertiesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_requests.IsSessionCurrent(request) || revision != _requests.Revision || _currentKey != expectedKey)
            return RecoveryProbe.Abort();
        if (!_requests.IsCurrent(request))
            return RecoveryProbe.Retry();

        var metadata = policy.NormalizeMetadata(new PlaybackMetadata(
            props.Title ?? string.Empty,
            props.Artist ?? string.Empty,
            props.AlbumTitle ?? string.Empty,
            null));
        metadata = metadata with { DurationMs = ReadDurationMs(metadata.Title, metadata.Artist) };
        if (string.IsNullOrWhiteSpace(metadata.Title))
            return RecoveryProbe.Retry();

        var query = new LyricsQuery(
            metadata.Title,
            metadata.Artist,
            metadata.DurationMs,
            metadata.Album,
            PreferredLyricsSource(_sourcePolicy.Key, _sourceAppUserModelId));
        if (query.Key == expectedKey)
            _requests.ConfirmTrack(query);
        return query.Key == expectedKey
            ? RecoveryProbe.Ready(query)
            : RecoveryProbe.Abort();
    }

    /// <summary>媒体区：上一首</summary>
    public async Task TrySkipPreviousAsync()
    {
        if (_session is null)
            return;
        try
        {
            await _session.TrySkipPreviousAsync();
        }
        catch
        {
            // SMTC 控制调用失败静默
        }
    }

    /// <summary>媒体区：播放/暂停</summary>
    public async Task TryTogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null)
            return;
        try
        {
            await session.TryTogglePlayPauseAsync();
        }
        catch
        {
            // SMTC 控制调用失败静默
        }
    }

    /// <summary>媒体区：下一首</summary>
    public async Task TrySkipNextAsync()
    {
        if (_session is null)
            return;
        try
        {
            await _session.TrySkipNextAsync();
        }
        catch
        {
            // SMTC 控制调用失败静默
        }
    }

    /// <summary>进入"等待新歌"状态：清显示、记录触发点，并主动轮询媒体属性</summary>
    private void BeginTrackSwitch(string reason)
    {
        int? initialPositionMs = null;
        if (string.IsNullOrEmpty(_currentKey) && _session is not null)
        {
            try
            {
                initialPositionMs = (int)Math.Clamp(
                    _session.GetTimelineProperties().Position.TotalMilliseconds,
                    0,
                    int.MaxValue);
            }
            catch
            {
                // 时间线暂时不可用时仍走原有墙钟初始化。
            }
        }

        _switchFsm.Begin(_currentKey, Environment.TickCount);
        _lastSwitchStartUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _positionTracker.OnTrackSwitch(_lastSwitchStartUtcMs, initialPositionMs);
        TrackSwitchStarted?.Invoke();
        StatusChanged?.Invoke("正在获取歌词…");
        AppLog.Debug($"切歌信号触发（{reason}），等待新歌 key='{_switchFsm.SwitchKey}'");
        _ = PollForNewTrackAsync();
    }

    /// <summary>
    ///     播放位置大幅回退但时长未变：可能是真切歌（两首歌同时长），也可能是向后拖动进度条。
    ///     不立即清显示，先静默轮询确认歌曲 key 真的变化（且连续两次稳定）再切换。
    /// </summary>
    private void SuspectTrackSwitch()
    {
        if (_switchFsm.IsSuspected)
            return;
        _switchFsm.Suspect();
        AppLog.Debug("位置回退疑似切歌，等待确认（区分拖动进度条）");
        _ = PollForTrackConfirmationAsync();
    }

    /// <summary>疑似切歌确认：连续两次读到同一个新 key 才算确认，之后走完整切换流程；否则静默作罢</summary>
    private async Task PollForTrackConfirmationAsync()
    {
        try
        {
            string? candidateKey = null;
            var stableCount = 0;
            for (var i = 0; i < _sourcePolicy.Switch.ConfirmationMaxAttempts; i++)
            {
                await Task.Delay(_sourcePolicy.Switch.ConfirmationPollIntervalMs);
                if (!_switchFsm.IsSuspected || _session is null || IsSwitchingTrack)
                    return; // 怀疑被取消 / 会话关闭 / 已被时长变化路径接管

                string? currentKey = null;
                try
                {
                    var props = await _session.TryGetMediaPropertiesAsync();
                    if (!string.IsNullOrWhiteSpace(props.Title))
                        currentKey = TrackKey.Of(props.Title, props.Artist);
                }
                catch
                {
                    // 读取失败继续等下一轮
                }

                if (currentKey is null || currentKey == _currentKey)
                {
                    candidateKey = null; // 元数据未就绪 / 仍是旧歌 → 重置候选
                    stableCount = 0;
                    continue;
                }

                if (candidateKey == currentKey)
                {
                    stableCount++;
                    if (stableCount < _sourcePolicy.Switch.ConfirmationRequiredCount)
                        continue;

                    // 同一个新 key 连续稳定读到 → 确认切歌，走完整流程
                    AppLog.Debug("位置回退确认切歌，进入切换流程");
                    _switchFsm.ClearSuspicion();
                    await RefreshTrackAsync();
                    return;
                }

                candidateKey = currentKey;
                stableCount = 1;
            }
        }
        catch
        {
            // 确认失败不影响事件驱动路径
        }
        finally
        {
            _switchFsm.ClearSuspicion();
        }
    }

    /// <summary>
    ///     切歌早期信号已触发，但 MediaPropertiesChanged 事件可能滞后几百毫秒：
    ///     主动轮询媒体属性，歌名/歌手一变就立即开始搜歌词。
    /// </summary>
    private async Task PollForNewTrackAsync()
    {
        try
        {
            for (var i = 0; i < _sourcePolicy.Switch.MetadataPollMaxAttempts; i++)
            {
                await Task.Delay(_sourcePolicy.Switch.MetadataPollIntervalMs);
                if (!IsSwitchingTrack || _session is null)
                {
                    AppLog.Debug($"元数据轮询结束（i={i}），switching={IsSwitchingTrack}");
                    return; // 歌词已加载 / 误判已恢复 / 会话关闭
                }

                await RefreshTrackAsync();
            }

            AppLog.Debug("元数据轮询 4s 超时，交由 ticker 看门狗接管");
        }
        catch
        {
            // 轮询失败不影响事件驱动路径
        }
    }

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() => HandleTimelinePropertiesChanged(sender));
    }

    private void HandleTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender)
    {
        var session = _session;
        if (session is null || !ReferenceEquals(sender, session))
            return;

        // SMTC 事件统一诊断：时间线推送（5s 节流，防高频推送的播放器刷屏）
        if (Environment.TickCount - _lastTimelineEventLogMs > 5000)
        {
            _lastTimelineEventLogMs = Environment.TickCount;
            try
            {
                var tl = session.GetTimelineProperties();
                AppLog.Debug(
                    $"时间线事件: 位置={(int)tl.Position.TotalMilliseconds}ms " +
                    $"时长={(int)tl.EndTime.TotalMilliseconds}ms " +
                    $"快照距今={(long)(DateTimeOffset.Now - tl.LastUpdatedTime).TotalMilliseconds}ms");
            }
            catch
            {
                // 诊断失败静默
            }
        }

        double endMs;
        try
        {
            endMs = session.GetTimelineProperties().EndTime.TotalMilliseconds;
        }
        catch
        {
            return;
        }

        // 前后都有有效时长且明显不同 → 切歌早期信号（歌词就绪后 2s 内的迟到时长变化不再重复触发）
        if (_sourcePolicy.Switch.UseDurationChangeAsEarlySignal &&
            _lastEndTimeMs > 0 && endMs > 0 &&
            Math.Abs(endMs - _lastEndTimeMs) >
            _sourcePolicy.Switch.DurationChangeThresholdMs &&
            !IsSwitchingTrack && !RecentlySwitched())
            BeginTrackSwitch("时长变化");

        if (endMs > 0)
            _lastEndTimeMs = endMs;
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() => HandlePlaybackInfoChanged(sender));
    }

    private void HandlePlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender)
    {
        var session = _session;
        if (session is null || !ReferenceEquals(sender, session))
            return;

        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        try
        {
            status = session.GetPlaybackInfo().PlaybackStatus;
        }
        catch
        {
            return;
        }

        var previousEvent = _lastEventPlaybackStatus;
        UpdatePlaybackCapabilities(session);
        var stateChanged = ApplyPlaybackState(
            status,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var eventChanged = status != previousEvent;
        _lastEventPlaybackStatus = status;

        // SMTC 允许重复推送同一状态（QQ 切歌同一轮会推多次），不重复触发切歌流程
        if (!eventChanged)
        {
            if (stateChanged)
                PlaybackStatusChanged?.Invoke(PlaybackState);
            return;
        }

        AppLog.Debug($"播放状态变化: {previousEvent} → {status}");

        // 从未播放/停止状态开始播放且当前没有歌词 → 主动刷新加载。
        // 播放开始时 SMTC 可能只推 PlaybackInfoChanged 而不推 MediaPropertiesChanged，
        // 不主动刷的话歌词永远不会加载（待机分支不进入切歌流程）。
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
            Lyrics.Lines.Count == 0)
            _ = RefreshTrackAsync();

        // 切歌早期信号：旧歌停止（Stopped）→ 新歌加载（Changing），立即清旧歌词进入等待。
        // 拖动进度条只改 Position，不会触发 PlaybackInfoChanged，天然不会误判。
        var isEarlySwitchSignal =
            (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped &&
             _sourcePolicy.Switch.UseStoppedAsEarlySignal) ||
            (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing &&
             _sourcePolicy.Switch.UseChangingAsEarlySignal);

        if (!IsSwitchingTrack && !RecentlySwitched() &&
            isEarlySwitchSignal)
            BeginTrackSwitch($"播放状态 {status}");

        // 停止后恢复播放同一首歌（单曲循环/手动停止再播）：不是切歌，立即恢复正常显示
        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
            IsSwitchingTrack && _currentKey == _switchFsm.SwitchKey && Lyrics.Lines.Count > 0)
        {
            AppLog.Debug("恢复播放同一首歌，取消等待状态");
            _switchFsm.Cancel();
            StatusChanged?.Invoke(string.Empty);
        }

        PlaybackStatusChanged?.Invoke(PlaybackState);
    }

    /// <summary>刚完成过切歌（2 秒内）：同一轮切歌序列的迟到信号应忽略</summary>
    public bool RecentlySwitched()
    {
        return _switchFsm.RecentlyCompleted(
            Environment.TickCount,
            _sourcePolicy.Switch.RecentSwitchIgnoreMs);
    }

    /// <summary>
    ///     ticker 每拍上报原始播放位置：负责切歌检测（位置回退）、误判恢复与看门狗。
    ///     UI 不再直接维护切换状态。
    /// </summary>
    private void NotifyPlaybackPosition(int rawPositionMs)
    {
        if (_session is null)
            return;

        // 播放中位置大幅回退（>2s）→ 疑似切歌；但拖动进度条也会回退，所以只进入"怀疑"状态
        var jumpedBack =
            _sourcePolicy.Switch.UsePositionRegressionAsEarlySignal &&
            _lastTickPositionMs >= 0 &&
            rawPositionMs < _lastTickPositionMs -
            _sourcePolicy.Switch.PositionRegressionThresholdMs;
        if (jumpedBack && !IsSwitchingTrack)
            SuspectTrackSwitch();
        _lastTickPositionMs = rawPositionMs;

        if (!IsSwitchingTrack)
            return;

        var elapsed = _switchFsm.ElapsedMs(Environment.TickCount);

        // 新歌已确认（key 已变化）→ 保持占位，等歌词加载完成；
        // 超过 8 秒仍未完成 → 看门狗强制复位，后台加载完成会自行刷新。
        if (_currentKey != _switchFsm.SwitchKey)
        {
            if (elapsed > _sourcePolicy.Switch.SwitchWatchdogMs)
            {
                AppLog.Warn(
                    "看门狗：歌词加载超时 {TimeoutMs}ms，复位切换状态",
                    _sourcePolicy.Switch.SwitchWatchdogMs);
                _switchFsm.Cancel();
                if (Lyrics.Lines.Count == 0)
                    StatusChanged?.Invoke(string.Empty); // 清掉"正在获取歌词…"占位
            }

            return;
        }

        // 3 秒内没有新歌确认 → 大概率是拖动进度条误判，恢复正常显示
        if (elapsed > _sourcePolicy.Switch.FalseSwitchRecoveryMs)
        {
            AppLog.Debug("误判恢复：3s 内未确认新歌，恢复正常显示");
            _switchFsm.Cancel();
            if (Lyrics.Lines.Count == 0)
                StatusChanged?.Invoke(string.Empty); // 清掉"正在获取歌词…"占位
        }
    }

    /// <summary>读取当前时间线（失败返回 null）</summary>
    private GlobalSystemMediaTransportControlsSessionTimelineProperties? GetTimeline()
    {
        try
        {
            return _session?.GetTimelineProperties();
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePlaybackCapabilities(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var controls = session.GetPlaybackInfo().Controls;
            SetPlaybackCapabilities(new PlaybackCapabilities(
                SupportsPrevious: controls.IsPreviousEnabled,
                SupportsPlayPause: controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                SupportsNext: controls.IsNextEnabled,
                SupportsSeek: controls.IsPlaybackPositionEnabled,
                ProvidesCover: _sourceAdapter.ProvidesCover));
        }
        catch
        {
            SetPlaybackCapabilities(_sourcePolicy.Capabilities with
            {
                ProvidesCover = _sourceAdapter.ProvidesCover
            });
        }
    }

    private void SetPlaybackCapabilities(PlaybackCapabilities capabilities)
    {
        if (Capabilities == capabilities)
            return;

        Capabilities = capabilities;
        AppLog.Info(
            $"播放能力: 上一首={capabilities.SupportsPrevious} 播放暂停={capabilities.SupportsPlayPause} " +
            $"下一首={capabilities.SupportsNext} 进度跳转={capabilities.SupportsSeek} 封面={capabilities.ProvidesCover}");
        PlaybackCapabilitiesChanged?.Invoke(capabilities);
    }

    /// <summary>曲目修订独立于歌词 generation；元数据序号只约束媒体回包，不打断同曲歌词加载。</summary>
    internal sealed class MediaRequestIdentity
    {
        private object? _session;
        private long _epoch;
        private long _metadataSequence;
        private LyricsQuery? _query;
        private bool _metadataReady;

        public long Revision { get; private set; }

        public void ChangeSession(object? session)
        {
            _session = session;
            _epoch++;
            _metadataSequence++;
            Revision++;
            _query = null;
            _metadataReady = false;
        }

        public MetadataRequest BeginMetadata(object session)
            => new(session, _epoch, ++_metadataSequence);

        public MetadataRequest Capture(object session) => new(session, _epoch, _metadataSequence);

        public bool IsSessionCurrent(MetadataRequest request)
            => ReferenceEquals(_session, request.Session) && request.Epoch == _epoch;

        public bool IsCurrent(MetadataRequest request)
            => IsSessionCurrent(request) && request.Sequence == _metadataSequence;

        public bool ConfirmTrack(LyricsQuery query)
        {
            var changed = _query?.Key != query.Key;
            if (changed)
                Revision++;
            _query = query;
            _metadataReady = !string.IsNullOrWhiteSpace(query.Title);
            return changed;
        }

        public void SetMetadataUnavailable() => _metadataReady = false;

        public LyricsSelectionContext? CreateContext()
            => _session is not null && _metadataReady && _query is not null
                ? new LyricsSelectionContext(_query, Revision)
                : null;

        public bool IsTrackCurrent(LyricsSelectionContext context)
            => _session is not null && context.Revision == Revision && context.Key == _query?.Key;

        public bool IsSelectionCurrent(LyricsSelectionContext context)
            => _metadataReady && IsTrackCurrent(context);

        internal readonly record struct MetadataRequest(object Session, long Epoch, long Sequence);
    }

    /// <summary>
    ///     统一应用 SMTC 原始播放状态，并在“非播放 → 播放”边界更新位置追踪器。
    ///     返回映射后的项目状态是否发生变化。
    /// </summary>
    private bool ApplyPlaybackState(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status,
        long nowUtcMs)
    {
        var previous = _lastPlaybackStatus;
        _lastPlaybackStatus = status;

        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
            previous.HasValue &&
            previous.Value != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            _positionTracker.OnResumeBoundary(nowUtcMs);

        var mapped = PlaybackStateMapper.Map(status);
        if (_nowPlaying is not null && _nowPlaying.PlaybackState != mapped)
            _nowPlaying = _nowPlaying with { PlaybackState = mapped };

        if (PlaybackState == mapped)
            return false;

        PlaybackState = mapped;
        return true;
    }
}
