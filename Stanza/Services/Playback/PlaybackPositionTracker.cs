namespace Stanza.Services.Playback;

/// <summary>
///     播放位置真相机：把 SMTC 时间线快照与播放状态合成为当前播放位置。
///     具体阈值由播放源策略提供，本类只负责公共时序逻辑。
/// </summary>
internal sealed class PlaybackPositionTracker(
    PlaybackTimingPolicy timing,
    PlaybackSwitchPolicy switching)
{
    private long _lastSwitchStartUtcMs;
    private long _resumeBoundaryUtcMs;

    private long _lastPlayingSnapshotUtcMs;
    private int _lastPlayingPositionMs = -1;
    private int _pausedDisplayPositionMs = -1;
    private int _resumeAnchorPositionMs = -1;
    private bool _trackConfirmed;

    /// <summary>切歌：重置全部时序状态；切歌起点同时作为新的外推边界。</summary>
    public void OnTrackSwitch(long utcMs, int? initialPositionMs = null)
    {
        _lastSwitchStartUtcMs = initialPositionMs.HasValue ? 0 : utcMs;
        _resumeBoundaryUtcMs = utcMs;
        _lastPlayingSnapshotUtcMs = 0;
        _lastPlayingPositionMs = initialPositionMs ?? -1;
        _pausedDisplayPositionMs = -1;
        _resumeAnchorPositionMs = initialPositionMs ?? -1;

        // 首次接入会话时带 initialPositionMs，不存在“上一首旧快照”问题，可直接视为已确认。
        // 真正的切歌必须等 SmtcSessionService 读到不同的新 key 后调用 OnTrackConfirmed()。
        _trackConfirmed = initialPositionMs.HasValue;
    }

    /// <summary>新歌 key 已确认：允许旧快照期间用墙钟从切歌点补位置。</summary>
    public void OnTrackConfirmed() => _trackConfirmed = true;

    /// <summary>恢复播放边界更新（暂停→播放）。</summary>
    public void OnResumeBoundary(long utcMs)
    {
        _resumeBoundaryUtcMs = utcMs;
        _resumeAnchorPositionMs = _pausedDisplayPositionMs >= 0
            ? _pausedDisplayPositionMs
            : _lastPlayingPositionMs;

        // 绝对不能在这里清 _lastSwitchStartUtcMs。切歌序列末尾的 Changing→Playing /
        // Stopped→Playing 也会走到这里，清零会同时废掉旧快照闸门和位置毛刺闸门。
    }

    /// <summary>会话断开/歌曲清空：闸门与合成状态全部复位。</summary>
    public void Reset()
    {
        _lastSwitchStartUtcMs = 0;
        _resumeBoundaryUtcMs = 0;
        _lastPlayingSnapshotUtcMs = 0;
        _lastPlayingPositionMs = -1;
        _pausedDisplayPositionMs = -1;
        _resumeAnchorPositionMs = -1;
        _trackConfirmed = false;
    }

    /// <summary>
    ///     每拍采样：输入原始时间线快照与播放状态，输出渲染决策和合成位置。
    /// </summary>
    public SampleResult Sample(
        long rawPositionMs,
        long snapshotUtcMs,
        bool isPlaying,
        long nowUtcMs)
    {
        // 闸门 1：切歌后的旧快照。只在策略窗口内拦截，超时后放行，
        // 兼容不周期更新 LastUpdatedTime 的播放器。
        if (_lastSwitchStartUtcMs > 0 &&
            nowUtcMs - _lastSwitchStartUtcMs < switching.StaleSnapshotWindowMs &&
            snapshotUtcMs < _lastSwitchStartUtcMs)
        {
            // 新歌已经进入 Playing，但时间线仍是上一首的旧快照。
            // 此时不要跳过渲染，否则要等整个旧快照窗口结束才从 0 开始补，
            // 表现为暂停后切歌的歌词延迟数秒。墙钟时间从切歌点直接推进。
            if (_trackConfirmed && isPlaying && timing.AllowWallClockFallback)
            {
                var elapsedMs = Math.Clamp(
                    nowUtcMs - _lastSwitchStartUtcMs,
                    0,
                    int.MaxValue);
                _pausedDisplayPositionMs = -1;
                _lastPlayingPositionMs = (int)elapsedMs;
                return SampleResult.Render(_lastPlayingPositionMs);
            }

            return SampleResult.Skip($"旧快照(滞后{_lastSwitchStartUtcMs - snapshotUtcMs}ms)");
        }

        // 闸门 2：LastUpdatedTime 已更新，但 Position 仍残留上一首的大值。
        if (rawPositionMs > switching.PositionGlitchThresholdMs &&
            nowUtcMs - _lastSwitchStartUtcMs < switching.PositionGlitchWindowMs)
            return SampleResult.Skip($"位置毛刺({rawPositionMs}ms)");

        return SampleResult.Render(
            ComputePosition(rawPositionMs, snapshotUtcMs, isPlaying, nowUtcMs));
    }

    private int ComputePosition(
        long rawPositionMs,
        long snapshotUtcMs,
        bool isPlaying,
        long nowUtcMs)
    {
        var positionMs = rawPositionMs;

        if (isPlaying)
        {
            _pausedDisplayPositionMs = -1;

            var elapsedMs = nowUtcMs - snapshotUtcMs;
            var snapshotIsFresh =
                timing.TrustLastUpdatedTime &&
                timing.AllowFreshSnapshotExtrapolation &&
                elapsedMs > 0 &&
                elapsedMs < timing.FreshSnapshotMaxAgeMs &&
                snapshotUtcMs >= _resumeBoundaryUtcMs;

            if (snapshotIsFresh)
            {
                positionMs += elapsedMs;
                _resumeAnchorPositionMs = -1;
            }
            else if (timing.AllowWallClockFallback &&
                     (!timing.TrustLastUpdatedTime || snapshotUtcMs < _resumeBoundaryUtcMs))
            {
                if (_resumeAnchorPositionMs < 0)
                    _resumeAnchorPositionMs = Math.Max(0, _lastPlayingPositionMs);

                // 恢复当拍 sinceResumeMs == 0 也必须落到锚点；保留 rawPositionMs
                // 会渲染暂停前或上一首的旧快照值，造成一帧位置跳变。
                var sinceResumeMs = Math.Max(0, nowUtcMs - _resumeBoundaryUtcMs);
                positionMs = _resumeAnchorPositionMs + sinceResumeMs;
            }

            _lastPlayingSnapshotUtcMs = snapshotUtcMs;
            _lastPlayingPositionMs = (int)Math.Clamp(positionMs, 0, int.MaxValue);
        }
        else
        {
            if (_pausedDisplayPositionMs < 0)
            {
                var finalPositionArrived =
                    timing.TrustPositionWhenPaused &&
                    (!timing.TrustLastUpdatedTime ||
                     snapshotUtcMs > _lastPlayingSnapshotUtcMs);

                var frozenPositionMs = finalPositionArrived
                    ? (int)Math.Min(rawPositionMs, int.MaxValue)
                    : _lastPlayingPositionMs >= 0
                        ? _lastPlayingPositionMs
                        : (int)Math.Min(rawPositionMs, int.MaxValue);

                // 暂停瞬间播放器可能先给旧快照、再补真实暂停位置。
                // 小幅回退不更新显示，避免卡拉OK条先外推、随后又跳回去；大回退仍视为 seek。
                if (_lastPlayingPositionMs >= 0 &&
                    frozenPositionMs < _lastPlayingPositionMs &&
                    _lastPlayingPositionMs - frozenPositionMs <= timing.PauseSeekThresholdMs)
                {
                    frozenPositionMs = _lastPlayingPositionMs;
                }

                _pausedDisplayPositionMs = frozenPositionMs;
                _lastPlayingSnapshotUtcMs = snapshotUtcMs;
            }
            else if (timing.TrustPositionWhenPaused &&
                     (!timing.TrustLastUpdatedTime ||
                      snapshotUtcMs > _lastPlayingSnapshotUtcMs))
            {
                var seekDelta = Math.Abs(rawPositionMs - _pausedDisplayPositionMs);
                if (seekDelta > timing.PauseSeekThresholdMs)
                {
                    _pausedDisplayPositionMs = (int)Math.Min(rawPositionMs, int.MaxValue);
                    _lastPlayingPositionMs = _pausedDisplayPositionMs;
                    _lastPlayingSnapshotUtcMs = snapshotUtcMs;
                }
            }

            positionMs = _pausedDisplayPositionMs;
        }

        return (int)Math.Clamp(positionMs, 0, int.MaxValue);
    }
}

/// <summary>一次采样的渲染决策。</summary>
internal readonly record struct SampleResult(
    int PositionMs,
    bool ShouldRender,
    string? SkipReason)
{
    public static SampleResult Render(int positionMs) =>
        new(positionMs, true, null);

    public static SampleResult Skip(string reason) =>
        new(0, false, reason);
}
