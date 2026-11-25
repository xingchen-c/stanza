using Stanza.Core.Models;
using Stanza.Core.Lyrics;
using Stanza.Core.Playback;

namespace Stanza.Core.Workflows;

/// <summary>协调播放观察、切歌确认与歌词加载，平台细节由端口提供。</summary>
internal sealed class PlaybackWorkflow : IDisposable
{
    private IPlaybackSession? _session;
    private readonly IPlaybackGateway _selector;
    private readonly IStateDispatcher _dispatcher;
    private readonly PlaybackContext _state;
    private readonly WorkflowTasks _tasks;
    private readonly LyricsWorkflow _lyricsCoordinator;
    private readonly CoverWorkflow _coverPipeline;
    private IPlaybackSourceAdapter _sourceAdapter = PlaybackSourceRegistry.ResolveAdapterByKey(PlaybackSourceIds.AutoFallback);
    private bool _disposed;

    public PlaybackState PlaybackState { get => _state.PlaybackState; private set => _state.PlaybackState = value; }
    public PlaybackCapabilities Capabilities { get => _state.Capabilities; private set => _state.Capabilities = value; }
    public bool IsSwitchingTrack => _state.Switch.IsSwitching;
    private LyricsLoader Lyrics => _lyricsCoordinator.Lyrics;

    public event Action? Changed;
    private void SetStatus(string text) { _state.Status = text; Changed?.Invoke(); }
    private void NotifySession(bool connected) { _state.Connected = connected; Changed?.Invoke(); }

    internal PlaybackWorkflow(IStateDispatcher dispatcher, IPlaybackGateway gateway,
        PlaybackContext state, LyricsWorkflow lyrics,
        CoverWorkflow cover, WorkflowTasks tasks)
    {
        _dispatcher = dispatcher;
        _selector = gateway;
        _state = state;
        _lyricsCoordinator = lyrics;
        _coverPipeline = cover;
        _tasks = tasks;
        _lyricsCoordinator.ResultApplied += OnLyricsResultApplied;
        _lyricsCoordinator.StatusChanged += SetStatus;
        _selector.SelectionChanged += OnSessionSelectionChanged;
        _selector.InitializationFailed += SetStatus;
    }

    /// <summary>初始化 SMTC 管理器并接入当前会话</summary>
    public Task InitializeAsync() => _selector.InitializeAsync(_tasks.Token);

    /// <summary>更新播放源模式并立即重新选择会话。</summary>
    public void SetPlaybackSource(string? mode) => _selector.SetPlaybackSource(mode);

    private void OnSessionSelectionChanged(PlaybackSelection? selection)
    {
        if (_disposed)
            return;
        if (selection is not { } selected)
        {
            if (_session is not null)
                UpdateSession(null);

            var waitingText = BuildWaitingText(_selector.PlaybackSourceMode);
            if (string.Equals(waitingText, _state.LastNoSessionStatus, StringComparison.Ordinal))
                return;

            _state.LastNoSessionStatus = waitingText;
            SetStatus(waitingText);
            return;
        }

        _state.LastNoSessionStatus = null;
        _state.SourcePolicy = selected.Policy;
        _sourceAdapter = selected.Adapter;
        _state.SourceAppUserModelId = selected.AppUserModelId;
        _coverPipeline.SetAdapter(_sourceAdapter);
        AppLog.Info(
            $"播放源策略: 模式={_selector.PlaybackSourceMode} 会话来源={_state.SourceAppUserModelId} " +
            $"策略={_state.SourcePolicy.Key}（{_state.SourcePolicy.DisplayName}）");

        _state.PositionTracker = new PlaybackPositionTracker(
            _state.SourcePolicy.Timing,
            _state.SourcePolicy.Switch);
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

    /// <summary>业务采样入口，会推进位置追踪和切歌判断；界面只能读取发布后的采样结果。</summary>
    internal void AdvancePlayback(long nowUtcMs)
    {
        if (_disposed) return;
        _state.Sample = SamplePlayback(nowUtcMs);
        Changed?.Invoke();
    }

    private PlaybackSample SamplePlayback(long nowUtcMs)
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
            var status = session.GetStatus();
            if (ApplyPlaybackState(status, nowUtcMs))
                Changed?.Invoke();
            state = PlaybackState;
        }
        catch
        {
            state = PlaybackState.Unknown;
        }

        var rawPositionMs = (int)timeline.Position.TotalMilliseconds;
        var result = _state.PositionTracker.Sample(
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
        if (_disposed) return;
        _disposed = true;
        _state.Requests.ChangeSession(null);
        Lyrics.InvalidateInFlight();
        _lyricsCoordinator.CancelRecovery();
        _selector.SelectionChanged -= OnSessionSelectionChanged;
        _selector.InitializationFailed -= SetStatus;
        _lyricsCoordinator.ResultApplied -= OnLyricsResultApplied;
        _lyricsCoordinator.StatusChanged -= SetStatus;
        _coverPipeline.Clear();

        if (_session is not null)
        {
            _session.MediaChanged -= OnMediaPropertiesChanged;
            _session.TimelineChanged -= OnTimelinePropertiesChanged;
            _session.PlaybackChanged -= OnPlaybackInfoChanged;
        }

        _session = null;
    }

    /// <summary>是否处于"正在播放"（含切歌中的 Changing）；查询失败按播放处理，走原有等待逻辑</summary>
    private static bool IsActuallyPlaying(IPlaybackSession session)
    {
        try
        {
            var status = session.GetStatus();
            return status is RawPlaybackStatus.Playing
                or RawPlaybackStatus.Changing;
        }
        catch
        {
            return true;
        }
    }

    private void UpdateSession(IPlaybackSession? session)
    {
        _lyricsCoordinator.CancelRecovery(); // 会话变化/断开：旧歌的恢复调度立即作废

        if (_session is not null)
        {
            _session.MediaChanged -= OnMediaPropertiesChanged;
            _session.TimelineChanged -= OnTimelinePropertiesChanged;
            _session.PlaybackChanged -= OnPlaybackInfoChanged;
        }

        _session = session;
        _state.Requests.ChangeSession(session);
        _state.CurrentKey = string.Empty;
        _state.NowPlaying = null;
        _state.Sample = new(0, 0, PlaybackState.Unknown, false, "会话已变化");
        _state.LyricsRequested = false;
        Lyrics.InvalidateInFlight();
        Lyrics.Clear();
        PlaybackState = PlaybackState.Unknown;
        SetPlaybackCapabilities(new PlaybackCapabilities(false, false, false, false, false));
        _state.LastTickPositionMs = -1;
        _state.LastEndTimeMs = -1;
        _state.DurationEvidence.Reset();
        _state.Switch.Reset();
        _state.LastPlaybackStatus = null;
        _state.LastEventPlaybackStatus = null;
        _state.LastSwitchStartUtcMs = 0;
        _state.PositionTracker.Reset();

        if (_session is null)
        {
            AppLog.Info("SMTC 会话断开");
            _coverPipeline.Clear();
            SetPlaybackCapabilities(new PlaybackCapabilities(false, false, false, false, false));
            NotifySession(false);
            SetStatus("等待播放音乐…");
            return;
        }

        AppLog.Info($"SMTC 会话接入: 来源={_state.SourceAppUserModelId}");
        _session.MediaChanged += OnMediaPropertiesChanged;
        _session.TimelineChanged += OnTimelinePropertiesChanged;
        _session.PlaybackChanged += OnPlaybackInfoChanged;
        UpdatePlaybackCapabilities(_session);

        // 缓存当前播放状态，供 PlaybackInfoChanged 去重比较
        try
        {
            var status = _session.GetStatus();
            ApplyPlaybackState(status, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _state.LastEventPlaybackStatus = status;
        }
        catch
        {
            // 某些播放器不提供播放信息，忽略
        }

        // 缓存当前歌曲时长，供"时长变化 = 切歌"早期信号比较
        try
        {
            var endMs = _session.GetTimeline().EndTime.TotalMilliseconds;
            if (endMs > 0)
                _state.LastEndTimeMs = endMs;
        }
        catch
        {
            // 某些播放器不提供时长，忽略
        }

        NotifySession(true);
        Changed?.Invoke();
        _ = _tasks.Run(RefreshTrackAsync);
    }

    /// <summary>切歌时读取歌曲信息，若和上次不同则重新搜索歌词</summary>
    public async Task RefreshTrackAsync()
    {
        var session = _session;
        if (session is null || _disposed)
            return;

        var request = _state.Requests.BeginMetadata(session);
        var policy = _state.SourcePolicy;
        int? lyricsGeneration = null;
        try
        {
            var props = await session.ReadMediaAsync(_tasks.Token);
            if (!_state.Requests.IsCurrent(request))
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
                PreferredLyricsSource(_state.SourcePolicy.Key, _state.SourceAppUserModelId));
            AppLog.Debug($"媒体属性: 标题={title} 歌手={artist} 封面引用={props.ReadCoverAsync is not null}");
            // 切歌瞬间元数据可能短暂为空：保留上一首的显示信息，避免悬停面板闪空歌名
            if (!string.IsNullOrWhiteSpace(title))
            {
                _state.NowPlaying = new NowPlaying(
                    TrackKey.Of(title, artist),
                    title,
                    artist,
                    metadata.Album,
                    metadata.DurationMs,
                    PlaybackState,
                    _state.SourcePolicy.Key,
                    _state.SourceAppUserModelId);
            }

            var playing = IsActuallyPlaying(session);
            if (!string.IsNullOrWhiteSpace(title))
            {
                if (_state.Requests.ConfirmTrack(lyricsQuery))
                {
                    _state.Sample = new(0, 0, PlaybackState, false, "等待新曲位置");
                    _lyricsCoordinator.CancelRecovery();
                    if (playing && !IsSwitchingTrack)
                        BeginTrackSwitch("元数据变化");
                    _state.CurrentKey = lyricsQuery.Key;
                    _state.LyricsRequested = false;
                    Lyrics.InvalidateInFlight();
                    Lyrics.Clear();
                    _state.PositionTracker.OnTrackConfirmed();
                }
            }
            else
            {
                _state.Requests.SetMetadataUnavailable();
            }

            // 未播放（暂停/停止/未开始）：待机状态——显示"歌名 - 歌手"或"等待播放音乐…"，
            // 不进入切歌等待/歌词加载流程（避免误导性的"正在获取歌词…"）。
            // 已有歌词（播放中暂停）保持显示，不覆盖。
            if (!playing)
            {
                _state.Switch.Cancel();
                var idleKey = string.IsNullOrWhiteSpace(title) ? string.Empty : TrackKey.Of(title, artist);
                _ = _tasks.Run(() => RefreshCoverWithRetryAsync(session, props, idleKey));
                if (Lyrics.Lines.Count == 0)
                    SetStatus(string.IsNullOrWhiteSpace(title)
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
            _ = _tasks.Run(() => RefreshCoverWithRetryAsync(session, props, key));
            if (_state.LyricsRequested)
                return;

            // 暂停时已确认的新歌在恢复播放后仍需加载，不能只按 key 相等提前返回。
            if (!IsSwitchingTrack)
                BeginTrackSwitch("开始加载");
            _state.PositionTracker.OnTrackConfirmed();
            _state.LyricsRequested = true;
            var context = _state.Requests.CreateContext()!;
            var generation = Lyrics.Generation;
            lyricsGeneration = generation;
            _state.Switch.MarkLoading();
            AppLog.Info($"开始加载歌词: {title} / {artist}");

            var result = await _lyricsCoordinator.LoadAsync(lyricsQuery);

            // 等待期间可能已经切到下一首歌，旧结果直接丢弃
            if (!_state.Requests.IsTrackCurrent(context) || !Lyrics.IsCurrentGeneration(generation))
                return;

            AppLog.Info($"歌词加载完成: {result}");
            _state.Switch.Complete(Environment.TickCount);

            // 歌词已就绪，但 SMTC 的位置快照可能还是切歌前的旧值（新快照未到）：
            // 此时先保留"正在获取歌词…"占位，避免加载完成后出现空白；
            // 新快照到达后 ticker 会立即按真实位置渲染。
            var snapshotReady = true;
            if (result == LyricsLoadResult.Loaded && _state.LastSwitchStartUtcMs > 0)
                try
                {
                    var timeline = session.GetTimeline();
                    snapshotReady = timeline.LastUpdatedTime.ToUnixTimeMilliseconds() >= _state.LastSwitchStartUtcMs;
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
            if (!_state.Requests.IsCurrent(request) ||
                (lyricsGeneration is { } generation && !Lyrics.IsCurrentGeneration(generation)))
                return;
            AppLog.Error($"读取媒体信息失败: {ex.Message}");
            SetStatus($"读取媒体信息失败：{ex.Message}");
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
            var timeline = _session?.GetTimeline();
            if (timeline is not null)
                return _state.DurationEvidence.Read(TrackKey.Of(title, artist),
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
    private Task RefreshCoverWithRetryAsync(IPlaybackSession session, PlaybackMedia props, string key)
        => _coverPipeline.RefreshWithRetryAsync(session, props, key, _state.Requests.Revision,
            () => ReferenceEquals(session, _session) && !_disposed, () => _state.Requests.Revision);

    private void OnMediaPropertiesChanged(IPlaybackSession sender) => _dispatcher.Post(() => { if (ReferenceEquals(sender, _session) && !_disposed) _ = _tasks.Run(RefreshTrackAsync); });

    /// <summary>媒体区开关：关闭时不再加载封面</summary>
    public void SetMediaControlsEnabled(bool enabled)
    {
        _coverPipeline.SetEnabled(enabled);
        if (enabled) _ = _tasks.Run(RefreshTrackAsync);
    }

    private void OnLyricsResultApplied(string key, LyricsLoadResult result)
    {
        _state.Switch.Complete(Environment.TickCount);
        Changed?.Invoke();
    }

    private async Task<RecoveryProbe> ReadCurrentQueryForRecoveryAsync(
        string expectedKey,
        CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null)
            return RecoveryProbe.Abort();

        var request = _state.Requests.Capture(session);
        var revision = _state.Requests.Revision;
        if (_state.CurrentKey != expectedKey)
            return RecoveryProbe.Abort();
        if (!IsActuallyPlaying(session))
            return RecoveryProbe.Retry();

        var policy = _state.SourcePolicy;
        var props = await session.ReadMediaAsync(_tasks.Token);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_state.Requests.IsSessionCurrent(request) || revision != _state.Requests.Revision || _state.CurrentKey != expectedKey)
            return RecoveryProbe.Abort();
        if (!_state.Requests.IsCurrent(request))
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
            PreferredLyricsSource(_state.SourcePolicy.Key, _state.SourceAppUserModelId));
        if (query.Key == expectedKey)
            _state.Requests.ConfirmTrack(query);
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
            await _session.PreviousAsync();
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
            await session.TogglePlayPauseAsync();
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
            await _session.NextAsync();
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
        if (string.IsNullOrEmpty(_state.CurrentKey) && _session is not null)
        {
            try
            {
                initialPositionMs = (int)Math.Clamp(
                    _session.GetTimeline().Position.TotalMilliseconds,
                    0,
                    int.MaxValue);
            }
            catch
            {
                // 时间线暂时不可用时仍走原有墙钟初始化。
            }
        }

        _state.Switch.Begin(_state.CurrentKey, Environment.TickCount);
        _state.LastSwitchStartUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _state.PositionTracker.OnTrackSwitch(_state.LastSwitchStartUtcMs, initialPositionMs);
        Changed?.Invoke();
        SetStatus("正在获取歌词…");
        AppLog.Debug($"切歌信号触发（{reason}），等待新歌 key='{_state.Switch.SwitchKey}'");
        _ = _tasks.Run(PollForNewTrackAsync);
    }

    /// <summary>
    ///     播放位置大幅回退但时长未变：可能是真切歌（两首歌同时长），也可能是向后拖动进度条。
    ///     不立即清显示，先静默轮询确认歌曲 key 真的变化（且连续两次稳定）再切换。
    /// </summary>
    private void SuspectTrackSwitch()
    {
        if (_state.Switch.IsSuspected)
            return;
        _state.Switch.Suspect();
        AppLog.Debug("位置回退疑似切歌，等待确认（区分拖动进度条）");
        _ = _tasks.Run(PollForTrackConfirmationAsync);
    }

    /// <summary>疑似切歌确认：连续两次读到同一个新 key 才算确认，之后走完整切换流程；否则静默作罢</summary>
    private async Task PollForTrackConfirmationAsync()
    {
        try
        {
            string? candidateKey = null;
            var stableCount = 0;
            for (var i = 0; i < _state.SourcePolicy.Switch.ConfirmationMaxAttempts; i++)
            {
                await Task.Delay(_state.SourcePolicy.Switch.ConfirmationPollIntervalMs, _tasks.Token);
                if (!_state.Switch.IsSuspected || _session is null || IsSwitchingTrack)
                    return; // 怀疑被取消 / 会话关闭 / 已被时长变化路径接管

                string? currentKey = null;
                try
                {
                    var props = await _session.ReadMediaAsync(_tasks.Token);
                    if (!string.IsNullOrWhiteSpace(props.Title))
                        currentKey = TrackKey.Of(props.Title, props.Artist);
                }
                catch
                {
                    // 读取失败继续等下一轮
                }

                if (currentKey is null || currentKey == _state.CurrentKey)
                {
                    candidateKey = null; // 元数据未就绪 / 仍是旧歌 → 重置候选
                    stableCount = 0;
                    continue;
                }

                if (candidateKey == currentKey)
                {
                    stableCount++;
                    if (stableCount < _state.SourcePolicy.Switch.ConfirmationRequiredCount)
                        continue;

                    // 同一个新 key 连续稳定读到 → 确认切歌，走完整流程
                    AppLog.Debug("位置回退确认切歌，进入切换流程");
                    _state.Switch.ClearSuspicion();
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
            _state.Switch.ClearSuspicion();
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
            for (var i = 0; i < _state.SourcePolicy.Switch.MetadataPollMaxAttempts; i++)
            {
                await Task.Delay(_state.SourcePolicy.Switch.MetadataPollIntervalMs, _tasks.Token);
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

    private void OnTimelinePropertiesChanged(IPlaybackSession sender) => _dispatcher.Post(() => { if (ReferenceEquals(sender, _session)) HandleTimelinePropertiesChanged(); });

    private void HandleTimelinePropertiesChanged()
    {
        var session = _session;
        if (session is null || _disposed)
            return;

        // SMTC 事件统一诊断：时间线推送（5s 节流，防高频推送的播放器刷屏）
        if (Environment.TickCount - _state.LastTimelineEventLogMs > 5000)
        {
            _state.LastTimelineEventLogMs = Environment.TickCount;
            try
            {
                var tl = session.GetTimeline();
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
            endMs = session.GetTimeline().EndTime.TotalMilliseconds;
        }
        catch
        {
            return;
        }

        // 前后都有有效时长且明显不同 → 切歌早期信号（歌词就绪后 2s 内的迟到时长变化不再重复触发）
        if (_state.SourcePolicy.Switch.UseDurationChangeAsEarlySignal &&
            _state.LastEndTimeMs > 0 && endMs > 0 &&
            Math.Abs(endMs - _state.LastEndTimeMs) >
            _state.SourcePolicy.Switch.DurationChangeThresholdMs &&
            !IsSwitchingTrack && !RecentlySwitched())
            BeginTrackSwitch("时长变化");

        if (endMs > 0)
            _state.LastEndTimeMs = endMs;
    }

    private void OnPlaybackInfoChanged(IPlaybackSession sender) => _dispatcher.Post(() => { if (ReferenceEquals(sender, _session)) HandlePlaybackInfoChanged(); });

    private void HandlePlaybackInfoChanged()
    {
        var session = _session;
        if (session is null || _disposed)
            return;

        RawPlaybackStatus status;
        try
        {
            status = session.GetStatus();
        }
        catch
        {
            return;
        }

        var previousEvent = _state.LastEventPlaybackStatus;
        UpdatePlaybackCapabilities(session);
        var stateChanged = ApplyPlaybackState(
            status,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var eventChanged = status != previousEvent;
        _state.LastEventPlaybackStatus = status;

        // SMTC 允许重复推送同一状态（QQ 切歌同一轮会推多次），不重复触发切歌流程
        if (!eventChanged)
        {
            if (stateChanged)
                Changed?.Invoke();
            return;
        }

        AppLog.Debug($"播放状态变化: {previousEvent} → {status}");

        // 从未播放/停止状态开始播放且当前没有歌词 → 主动刷新加载。
        // 播放开始时 SMTC 可能只推 PlaybackInfoChanged 而不推 MediaPropertiesChanged，
        // 不主动刷的话歌词永远不会加载（待机分支不进入切歌流程）。
        if (status == RawPlaybackStatus.Playing &&
            Lyrics.Lines.Count == 0)
            _ = _tasks.Run(RefreshTrackAsync);

        // 切歌早期信号：旧歌停止（Stopped）→ 新歌加载（Changing），立即清旧歌词进入等待。
        // 拖动进度条只改 Position，不会触发 PlaybackInfoChanged，天然不会误判。
        var isEarlySwitchSignal =
            (status == RawPlaybackStatus.Stopped &&
             _state.SourcePolicy.Switch.UseStoppedAsEarlySignal) ||
            (status == RawPlaybackStatus.Changing &&
             _state.SourcePolicy.Switch.UseChangingAsEarlySignal);

        if (!IsSwitchingTrack && !RecentlySwitched() &&
            isEarlySwitchSignal)
            BeginTrackSwitch($"播放状态 {status}");

        // 停止后恢复播放同一首歌（单曲循环/手动停止再播）：不是切歌，立即恢复正常显示
        if (status == RawPlaybackStatus.Playing &&
            IsSwitchingTrack && _state.CurrentKey == _state.Switch.SwitchKey && Lyrics.Lines.Count > 0)
        {
            AppLog.Debug("恢复播放同一首歌，取消等待状态");
            _state.Switch.Cancel();
            SetStatus(string.Empty);
        }

        Changed?.Invoke();
    }

    /// <summary>刚完成过切歌（2 秒内）：同一轮切歌序列的迟到信号应忽略</summary>
    public bool RecentlySwitched()
    {
        return _state.Switch.RecentlyCompleted(
            Environment.TickCount,
            _state.SourcePolicy.Switch.RecentSwitchIgnoreMs);
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
            _state.SourcePolicy.Switch.UsePositionRegressionAsEarlySignal &&
            _state.LastTickPositionMs >= 0 &&
            rawPositionMs < _state.LastTickPositionMs -
            _state.SourcePolicy.Switch.PositionRegressionThresholdMs;
        if (jumpedBack && !IsSwitchingTrack)
            SuspectTrackSwitch();
        _state.LastTickPositionMs = rawPositionMs;

        if (!IsSwitchingTrack)
            return;

        var elapsed = _state.Switch.ElapsedMs(Environment.TickCount);

        // 新歌已确认（key 已变化）→ 保持占位，等歌词加载完成；
        // 超过 8 秒仍未完成 → 看门狗强制复位，后台加载完成会自行刷新。
        if (_state.CurrentKey != _state.Switch.SwitchKey)
        {
            if (elapsed > _state.SourcePolicy.Switch.SwitchWatchdogMs)
            {
                AppLog.Warn(
                    "看门狗：歌词加载超时 {TimeoutMs}ms，复位切换状态",
                    _state.SourcePolicy.Switch.SwitchWatchdogMs);
                _state.Switch.Cancel();
                if (Lyrics.Lines.Count == 0)
                    SetStatus(string.Empty); // 清掉"正在获取歌词…"占位
            }

            return;
        }

        // 3 秒内没有新歌确认 → 大概率是拖动进度条误判，恢复正常显示
        if (elapsed > _state.SourcePolicy.Switch.FalseSwitchRecoveryMs)
        {
            AppLog.Debug("误判恢复：3s 内未确认新歌，恢复正常显示");
            _state.Switch.Cancel();
            if (Lyrics.Lines.Count == 0)
                SetStatus(string.Empty); // 清掉"正在获取歌词…"占位
        }
    }

    /// <summary>读取当前时间线（失败返回 null）</summary>
    private PlaybackTimeline? GetTimeline()
    {
        try
        {
            return _session?.GetTimeline();
        }
        catch
        {
            return null;
        }
    }

    private void UpdatePlaybackCapabilities(IPlaybackSession session)
    {
        try
        {
            SetPlaybackCapabilities(session.GetCapabilities() with { ProvidesCover = _sourceAdapter.ProvidesCover });
        }
        catch
        {
            SetPlaybackCapabilities(_state.SourcePolicy.Capabilities with
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
        Changed?.Invoke();
    }

    /// <summary>曲目修订独立于歌词 generation；元数据序号只约束媒体回包，不打断同曲歌词加载。</summary>
    /// <summary>
    ///     统一应用 SMTC 原始播放状态，并在“非播放 → 播放”边界更新位置追踪器。
    ///     返回映射后的项目状态是否发生变化。
    /// </summary>
    private bool ApplyPlaybackState(
        RawPlaybackStatus status,
        long nowUtcMs)
    {
        var previous = _state.LastPlaybackStatus;
        _state.LastPlaybackStatus = status;

        if (status == RawPlaybackStatus.Playing &&
            previous.HasValue &&
            previous.Value != RawPlaybackStatus.Playing)
            _state.PositionTracker.OnResumeBoundary(nowUtcMs);

        var mapped = PlaybackStateMapper.Map(status);
        if (_state.NowPlaying is not null && _state.NowPlaying.PlaybackState != mapped)
            _state.NowPlaying = _state.NowPlaying with { PlaybackState = mapped };

        if (PlaybackState == mapped)
            return false;

        PlaybackState = mapped;
        return true;
    }
}
