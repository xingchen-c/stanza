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

    /// <summary>切歌：重置全部时序状态；切歌起点同时作为新的外推边界。</summary>
    public void OnTrackSwitch(long utcMs)
    {
        _lastSwitchStartUtcMs = utcMs;
        _resumeBoundaryUtcMs = utcMs;
        _lastPlayingSnapshotUtcMs = 0;
        _lastPlayingPositionMs = -1;
        _pausedDisplayPositionMs = -1;
        _resumeAnchorPositionMs = -1;
    }

    /// <summary>恢复播放边界更新（暂停→播放）。</summary>
    public void OnResumeBoundary(long utcMs)
    {
        _resumeBoundaryUtcMs = utcMs;
        _resumeAnchorPositionMs = -1;
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
            return SampleResult.Skip($"旧快照(滞后{_lastSwitchStartUtcMs - snapshotUtcMs}ms)");

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
            if (_pausedDisplayPositionMs >= 0)
                _resumeAnchorPositionMs = -1;
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

                var sinceResumeMs = nowUtcMs - _resumeBoundaryUtcMs;
                if (sinceResumeMs > 0)
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

                _pausedDisplayPositionMs = finalPositionArrived
                    ? (int)Math.Min(rawPositionMs, int.MaxValue)
                    : _lastPlayingPositionMs >= 0
                        ? _lastPlayingPositionMs
                        : (int)Math.Min(rawPositionMs, int.MaxValue);
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
