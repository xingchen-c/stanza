namespace Stanza.Services;

/// <summary>
///     播放位置真相机：把 SMTC 时间线快照 + 播放状态合成为"当前播放位置"，
///     并在此收口切歌后的渲染闸门（旧快照窗口 / 位置毛刺）。
///     纯逻辑、无 UI 依赖；所有时序状态集中于此，不再散落 MainWindow / SmptcSessionService。
///
///     背景知识（改动前必读）：
///     - SMTC 的 Position 是"LastUpdatedTime 时刻"的快照，不是实时位置；QQ 约每秒推一次新快照。
///     - 部分播放器（实测：网易云桌面版）时间线完全退化：Position 恒 0、LastUpdatedTime 恒为
///       DateTimeOffset.MinValue（公元 1601 年）、TimelinePropertiesChanged 从不触发。
///       此时位置只能靠"恢复边界 + 墙钟锚点"本地合成。
///     - 锚点只在恢复/切歌时捕获一次，绝不随拍自喂（否则位置与累计时长叠加，呈二次爆炸）。
/// </summary>
internal sealed class PlaybackPositionTracker
{
    /// <summary>旧快照闸门窗口：切歌后只在该窗口内等新鲜快照；超时无条件放行
    /// （不周期推快照的播放器否则永远卡占位）</summary>
    private const long StaleSnapshotWindowMs = 3000;

    /// <summary>位置毛刺窗口：切歌后该窗口内出现的大位置视为上一首残留</summary>
    private const long PositionGlitchWindowMs = 2000;

    /// <summary>毛刺判定阈值：窗口内位置超过该值视为残留</summary>
    private const int PositionGlitchThresholdMs = 10000;

    /// <summary>暂停中接受拖动的最小位移（小于该值视为快照抖动，忽略）</summary>
    private const int PauseSeekThresholdMs = 2000;

    // ---- 事件锚点 ----
    private long _lastSwitchStartUtcMs; // 最近一次切歌起点（0 = 无）
    private long _resumeBoundaryUtcMs; // 恢复播放边界（切歌也视为一次从零恢复）

    // ---- 位置合成状态 ----
    private long _lastPlayingSnapshotUtcMs; // 播放中最近一次参与计算的快照时间
    private int _lastPlayingPositionMs = -1; // 播放中最近一次计算位置（-1 = 无）
    private int _pausedDisplayPositionMs = -1; // 暂停冻结位置（-1 = 非暂停）
    private int _resumeAnchorPositionMs = -1; // 恢复锚点（-1 = 未捕获）

    /// <summary>切歌：重置全部时序状态；切歌起点同时作为新的外推边界（新歌从零播放）</summary>
    public void OnTrackSwitch(long utcMs)
    {
        _lastSwitchStartUtcMs = utcMs;
        _resumeBoundaryUtcMs = utcMs;
        _lastPlayingSnapshotUtcMs = 0;
        _lastPlayingPositionMs = -1;
        _pausedDisplayPositionMs = -1;
        _resumeAnchorPositionMs = -1;
    }

    /// <summary>恢复播放边界更新（暂停→播放）。重复调用会强制重捕锚点，调用方必须过滤重复状态</summary>
    public void OnResumeBoundary(long utcMs)
    {
        _resumeBoundaryUtcMs = utcMs;
        _resumeAnchorPositionMs = -1;
    }

    /// <summary>会话断开/歌曲清空：闸门与合成状态全部复位</summary>
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
    ///     每拍采样：输入原始时间线快照与播放状态，输出渲染决策与合成位置。
    ///     ShouldRender=false 时带 SkipReason（诊断日志用）。
    /// </summary>
    public SampleResult Sample(long rawPositionMs, long snapshotUtcMs, bool isPlaying, long nowUtcMs)
    {
        // 闸门 1：旧快照。切歌刚完成时快照可能还停在切歌前的位置，用它匹配新歌词会闪错位行。
        // 只在窗口内拦截：不周期推快照的播放器（网易云 LastUpdatedTime 恒为 1601 年）窗口外放行。
        if (_lastSwitchStartUtcMs > 0 &&
            nowUtcMs - _lastSwitchStartUtcMs < StaleSnapshotWindowMs &&
            snapshotUtcMs < _lastSwitchStartUtcMs)
            return SampleResult.Skip($"旧快照(滞后{_lastSwitchStartUtcMs - snapshotUtcMs}ms)");

        // 闸门 2：位置毛刺。LastUpdatedTime 已更新但 Position 残留上一首的大值，
        // 直接渲染会让卡拉OK先滚一截再回退
        if (rawPositionMs > PositionGlitchThresholdMs &&
            nowUtcMs - _lastSwitchStartUtcMs < PositionGlitchWindowMs)
            return SampleResult.Skip($"位置毛刺({rawPositionMs}ms)");

        return SampleResult.Render(ComputePosition(rawPositionMs, snapshotUtcMs, isPlaying, nowUtcMs));
    }

    private int ComputePosition(long rawPositionMs, long snapshotUtcMs, bool isPlaying, long nowUtcMs)
    {
        var positionMs = rawPositionMs;

        if (isPlaying)
        {
            if (_pausedDisplayPositionMs >= 0)
                _resumeAnchorPositionMs = -1; // 恢复首拍：强制重捕锚点（恢复事件漏发的兜底）
            _pausedDisplayPositionMs = -1; // 恢复播放：解除暂停冻结（恢复锚点在下方重捕）

            var elapsedMs = nowUtcMs - snapshotUtcMs;
            if (elapsedMs > 0 && elapsedMs < 2000 && snapshotUtcMs >= _resumeBoundaryUtcMs)
            {
                // 新鲜快照（QQ 正常路径）：快照位置 + 距今外推
                positionMs += elapsedMs;
                _resumeAnchorPositionMs = -1; // 快照接管，锚点作废
            }
            else if (snapshotUtcMs < _resumeBoundaryUtcMs)
            {
                // 恢复后旧快照窗口（QQ，约 1 秒）或退化时间线（网易云，全程）：
                // 从恢复锚点做墙钟外推。锚点只捕获一次，绝不随拍自喂
                if (_resumeAnchorPositionMs < 0)
                    _resumeAnchorPositionMs = Math.Max(0, _lastPlayingPositionMs);
                var sinceResumeMs = nowUtcMs - _resumeBoundaryUtcMs;
                if (sinceResumeMs > 0)
                    positionMs = _resumeAnchorPositionMs + sinceResumeMs;
            }
            // 其余情况（快照新鲜度介于两者之间）：按快照原值渲染，不外推

            _lastPlayingSnapshotUtcMs = snapshotUtcMs;
            _lastPlayingPositionMs = (int)Math.Clamp(positionMs, 0, int.MaxValue);
        }
        else
        {
            // 暂停/停止：暂停瞬间对齐到最后外推位置（≈音频实际停止点），之后冻结显示——
            // 迟到的"最终位置"快照不再触发二次跳变；只接受明显的拖动进度条（大跳变）
            if (_pausedDisplayPositionMs < 0)
            {
                var finalPositionArrived = snapshotUtcMs > _lastPlayingSnapshotUtcMs;
                _pausedDisplayPositionMs = finalPositionArrived
                    ? (int)Math.Min(rawPositionMs, int.MaxValue)
                    : _lastPlayingPositionMs >= 0
                        ? _lastPlayingPositionMs
                        : (int)Math.Min(rawPositionMs, int.MaxValue);
                _lastPlayingSnapshotUtcMs = snapshotUtcMs;
            }
            else if (snapshotUtcMs > _lastPlayingSnapshotUtcMs)
            {
                var seekDelta = Math.Abs(rawPositionMs - _pausedDisplayPositionMs);
                if (seekDelta > PauseSeekThresholdMs)
                {
                    // 暂停中拖动进度条：接受新位置（恢复播放时从这里继续）
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

/// <summary>一次采样的渲染决策：ShouldRender=false 时带原因（诊断日志用）</summary>
internal readonly record struct SampleResult(int PositionMs, bool ShouldRender, string? SkipReason)
{
    public static SampleResult Render(int positionMs) => new(positionMs, true, null);
    public static SampleResult Skip(string reason) => new(0, false, reason);
}