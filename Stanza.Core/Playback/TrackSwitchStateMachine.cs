namespace Stanza.Core.Playback;

internal enum TrackSwitchState
{
    Idle,
    Suspected,
    Switching,
    Loading
}

/// <summary>
///     切歌状态机：只维护状态、时间点和当前切换 key，不负责歌词加载等副作用。
///     状态职责：
///     Idle      正常播放；
///     Suspected 位置回退疑似切歌，等待元数据确认；
///     Switching 已收到切歌信号，等待新 TrackKey；
///     Loading   新 TrackKey 已确认，等待歌词加载完成。
/// </summary>
internal sealed class TrackSwitchStateMachine
{
    public TrackSwitchState State { get; private set; } = TrackSwitchState.Idle;
    public string SwitchKey { get; private set; } = string.Empty;
    private int? _startedAt;
    private int? _lastCompletedAt;

    public bool IsSwitching => State is TrackSwitchState.Switching or TrackSwitchState.Loading;
    public bool IsSuspected => State == TrackSwitchState.Suspected;

    public void Suspect()
    {
        if (State == TrackSwitchState.Idle)
            State = TrackSwitchState.Suspected;
    }

    public void ClearSuspicion()
    {
        if (State == TrackSwitchState.Suspected)
            State = TrackSwitchState.Idle;
    }

    public void Begin(string currentKey, int nowTick)
    {
        State = TrackSwitchState.Switching;
        SwitchKey = currentKey;
        _startedAt = nowTick;
    }

    public void MarkLoading()
    {
        if (State == TrackSwitchState.Switching)
            State = TrackSwitchState.Loading;
    }

    public void Complete(int nowTick)
    {
        State = TrackSwitchState.Idle;
        _lastCompletedAt = nowTick;
    }

    public void Cancel()
    {
        if (State != TrackSwitchState.Idle)
            State = TrackSwitchState.Idle;
    }

    public void Reset()
    {
        State = TrackSwitchState.Idle;
        SwitchKey = string.Empty;
        _startedAt = null;
        _lastCompletedAt = null;
    }

    public int ElapsedMs(int nowTick)
        => _startedAt is { } startedAt ? unchecked(nowTick - startedAt) : 0;

    public bool RecentlyCompleted(int nowTick, int windowMs)
        => _lastCompletedAt is { } completedAt && unchecked((uint)(nowTick - completedAt)) < (uint)windowMs;
}
