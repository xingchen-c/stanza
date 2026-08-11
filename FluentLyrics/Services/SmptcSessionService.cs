using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using FluentLyrics.Models;
using FluentLyrics.Services.Lyrics;

namespace FluentLyrics.Services;

/// <summary>
///     SMTC 会话驱动：管理媒体会话生命周期、歌词加载、切歌状态机、封面解码与播放控制。
///     与 UI 解耦：通过事件向外通知状态变化，MainWindow 只订阅事件做渲染。
/// </summary>
public sealed class SmptcSessionService : IDisposable
{
    private readonly LyricsService _lyrics = new();
    private readonly Dispatcher _dispatcher;

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private string _currentKey = string.Empty;
    private string _currentTitle = string.Empty;
    private string _currentArtist = string.Empty;
    private bool _switchingTrack;
    private string _switchKey = string.Empty;
    private int _switchStartedAt;
    private bool _switchSuspected;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastPlaybackStatus;
    private long _lastSwitchStartUtcMs;
    private int _lastSwitchCompletedAt = -1;
    private double _lastEndTimeMs = -1;
    private int _lastTickPositionMs = -1;
    private CancellationTokenSource? _recoveryCts;        // "未找到歌词"恢复调度（快速重试 + 后台补抓）
    private string _coverKey = string.Empty;
    private bool _coverLoaded;
    private bool _mediaControlsEnabled;

    // "未找到歌词"后台补抓参数：只针对 NotFound（可能是临时失败）；
    // 纯音乐（Instrumental）/已加载（Loaded）是确定性结果，绝不补抓。
    private const int NotFoundRefetchIntervalSec = 15; // 补抓间隔（秒）
    private const int NotFoundRefetchMaxAttempts = 4;  // 补抓次数上限（约覆盖歌曲前 1 分钟）

    /// <summary>会话接入/断开（参数：是否已连接）</summary>
    public event Action<bool>? SessionChanged;

    /// <summary>需要显示的状态文本（"正在获取歌词…" / "未找到歌词" / 空串 = 恢复正常）</summary>
    public event Action<string>? StatusChanged;

    /// <summary>切歌开始：UI 应清掉当前渲染状态，等待新歌词</summary>
    public event Action? TrackSwitchStarted;

    /// <summary>某首歌的歌词加载完成（key + 结果）</summary>
    public event Action<string, LyricsLoadResult>? TrackLyricsChanged;

    /// <summary>播放状态变化（图标/显隐刷新用）</summary>
    public event Action<GlobalSystemMediaTransportControlsSessionPlaybackStatus?>? PlaybackStatusChanged;

    /// <summary>封面就绪/变化（key + 位图；null = 无封面或加载失败）</summary>
    public event Action<string, BitmapSource?>? CoverChanged;

    public SmptcSessionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>当前 SMTC 会话（UI 渲染时读取时间线等）</summary>
    public GlobalSystemMediaTransportControlsSession? Session => _session;

    /// <summary>当前歌曲标题（悬停面板显示用）</summary>
    public string CurrentTitle => _currentTitle;

    /// <summary>当前歌曲歌手（悬停面板显示用）</summary>
    public string CurrentArtist => _currentArtist;

    /// <summary>是否正在等待新歌歌词</summary>
    public bool IsSwitchingTrack => _switchingTrack;

    /// <summary>最近一次切歌开始（UTC 毫秒），与 SMTC Position 快照时间比较</summary>
    public long LastSwitchStartUtcMs => _lastSwitchStartUtcMs;

    /// <summary>歌词库（UI 渲染读取行数据）</summary>
    public LyricsService Lyrics => _lyrics;

    /// <summary>初始化 SMTC 管理器并接入当前会话</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
            var hasSession = _sessionManager.GetCurrentSession() is not null;
            AppLog.Info($"SMTC 初始化成功（当前会话: {(hasSession ? "有" : "无")}）");
            UpdateSession(_sessionManager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            AppLog.Error($"SMTC 初始化失败: {ex.Message}");
            StatusChanged?.Invoke($"SMTC 初始化失败：{ex.Message}");
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() => UpdateSession(_sessionManager?.GetCurrentSession()));
    }

    /// <summary>释放：退订 SMTC 事件、取消"未找到歌词"恢复调度（应用退出时调用）</summary>
    public void Dispose()
    {
        CancelNotFoundRecovery();

        if (_sessionManager is not null)
            _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _sessionManager = null;
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
        CancelNotFoundRecovery(); // 会话变化/断开：旧歌的恢复调度立即作废

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = session;
        _lastTickPositionMs = -1;
        _lastEndTimeMs = -1;
        _switchingTrack = false;
        _switchSuspected = false;
        _lastPlaybackStatus = null;
        _lastSwitchStartUtcMs = 0;
        _lastSwitchCompletedAt = -1;

        if (_session is null)
        {
            AppLog.Info("SMTC 会话断开");
            _lyrics.Clear();
            _currentKey = string.Empty;
            _currentTitle = string.Empty;
            _currentArtist = string.Empty;
            _coverKey = string.Empty;
            _coverLoaded = false;
            CoverChanged?.Invoke(string.Empty, null);
            SessionChanged?.Invoke(false);
            StatusChanged?.Invoke("等待播放音乐…");
            return;
        }

        AppLog.Info("SMTC 会话接入");
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;

        // 缓存当前播放状态，供 PlaybackInfoChanged 去重比较
        try
        {
            _lastPlaybackStatus = _session.GetPlaybackInfo().PlaybackStatus;
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
        PlaybackStatusChanged?.Invoke(_lastPlaybackStatus);
        _ = RefreshTrackAsync();
    }

    /// <summary>切歌时读取歌曲信息，若和上次不同则重新搜索歌词</summary>
    public async Task RefreshTrackAsync()
    {
        var session = _session;
        if (session is null)
            return;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var title = props.Title;
            var artist = props.Artist ?? string.Empty;
            // 切歌瞬间元数据可能短暂为空：保留上一首的显示信息，避免悬停面板闪空歌名
            if (!string.IsNullOrWhiteSpace(title))
            {
                _currentTitle = title;
                _currentArtist = artist;
            }

            // 未播放（暂停/停止/未开始）：待机状态——显示"歌名 - 歌手"或"等待播放音乐…"，
            // 不进入切歌等待/歌词加载流程（避免误导性的"正在获取歌词…"）。
            // 已有歌词（播放中暂停）保持显示，不覆盖。
            if (!IsActuallyPlaying(session))
            {
                _switchingTrack = false;
                var idleKey = string.IsNullOrWhiteSpace(title) ? string.Empty : TrackKey.Of(title, artist);
                _ = RefreshCoverAsync(props, idleKey);
                if (_lyrics.Lines.Count == 0)
                    StatusChanged?.Invoke(string.IsNullOrWhiteSpace(title)
                        ? "等待播放音乐…"
                        : string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}");
                return;
            }

            // 切歌瞬间 SMTC 元数据可能短暂为空：不能把空值当成"未知歌曲"去搜索，
            // 否则会污染当前歌曲 key，并让轮询误以为"新歌已确认"而提前终止。
            // 这里只等待，不更新任何状态，由轮询/后续事件重读。
            if (string.IsNullOrWhiteSpace(title))
            {
                AppLog.Debug("元数据未就绪（空标题），等待轮询重读");
                if (!_switchingTrack)
                    BeginTrackSwitch("元数据未就绪"); // 顺带启动元数据轮询，等真实歌名出现
                return;
            }

            var key = TrackKey.Of(title, artist);
            _ = RefreshCoverAsync(props, key); // 切歌换封面；key 未变时内部直接跳过
            if (key == _currentKey)
                return;

            // 确认切到新歌：取消旧歌的"未找到歌词"补抓循环
            CancelNotFoundRecovery();

            // 统一走 BeginTrackSwitch 记录切换起点（Stopped/时长变化可能已触发；元数据先到时这里兜底）
            if (!_switchingTrack)
                BeginTrackSwitch("元数据变化");

            _currentKey = key;
            AppLog.Info($"开始加载歌词: {title} / {artist}");

            var result = await LoadLyricsOnceAsync(title, artist);

            // 等待期间可能已经切到下一首歌，旧结果直接丢弃
            if (key != _currentKey)
                return;

            AppLog.Info($"歌词加载完成: {result}");
            _switchingTrack = false;
            _lastSwitchCompletedAt = Environment.TickCount;

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

            TrackLyricsChanged?.Invoke(key, result);
            // NotFound 先不显示"未找到歌词"：保留"正在获取歌词…"占位，等 1.2s 快速重试。
            // QQ 首次匹配常因切歌瞬间旧时长失败，立即显示"未找到"会造成闪烁。
            // Instrumental（纯音乐）是确定结果，照常立即显示。
            if (snapshotReady && result != LyricsLoadResult.NotFound)
                StatusChanged?.Invoke(StatusTextFor(result));

            // 偶发：切歌瞬间搜索失败 / QQ 接口限流。NotFound 不缓存，延迟重试一次，
            // 避免"未找到歌词"被定死整首歌（重试期间已有占位文本，无闪烁）。
            if (result == LyricsLoadResult.NotFound)
                ScheduleNotFoundRecovery(key, title, artist);
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取媒体信息失败: {ex.Message}");
            StatusChanged?.Invoke($"读取媒体信息失败：{ex.Message}");
        }
    }

    /// <summary>歌词加载结果的占位文案</summary>
    private static string StatusTextFor(LyricsLoadResult result)
    {
        return result switch
        {
            LyricsLoadResult.Loaded => string.Empty,
            LyricsLoadResult.Instrumental => "此歌曲为没有填词的纯音乐，请您欣赏",
            _ => "未找到歌词"
        };
    }

    /// <summary>读取当前歌曲时长（搜索匹配辅助；切歌瞬间 EndTime 可能还是旧值）</summary>
    private int? ReadDurationMs()
    {
        try
        {
            var endTime = _session?.GetTimelineProperties().EndTime;
            if (endTime is { } t && t > TimeSpan.Zero)
                return (int)t.TotalMilliseconds;
        }
        catch
        {
            // 读不到时长就按不带时长搜索
        }

        return null;
    }

    /// <summary>统一歌词加载入口：用户选择记忆优先 → 多源自动匹配</summary>
    private async Task<LyricsLoadResult> LoadLyricsOnceAsync(string title, string artist)
    {
        var key = TrackKey.Of(title, artist);
        if (LyricsChoiceService.TryGet(key, out var choice))
        {
            AppLog.Debug($"使用用户选择的歌词候选: '{choice.Title}' / {choice.Artist}（来源={choice.SourceName}）");
            var result = await _lyrics.LoadCandidateAsync(choice);
            if (result != LyricsLoadResult.NotFound)
                return result;
            AppLog.Warn("记忆的歌词候选加载失败，退回自动匹配");
        }

        return await _lyrics.LoadAsync(title, artist, ReadDurationMs());
    }

    /// <summary>统一应用歌词加载结果：重置切换状态、通知 UI、设置状态文本。
    /// showNotFound=false 时 NotFound 不显示（保留"正在获取歌词…"占位，等恢复调度确认）。</summary>
    private void ApplyLoadResult(string key, LyricsLoadResult result, bool showNotFound = true)
    {
        _switchingTrack = false;
        _lastSwitchCompletedAt = Environment.TickCount;
        TrackLyricsChanged?.Invoke(key, result);
        if (showNotFound || result != LyricsLoadResult.NotFound)
            StatusChanged?.Invoke(StatusTextFor(result));
    }

    /// <summary>启动"未找到歌词"恢复调度：1.2s 快速重试 → 仍失败接 15s×4 后台补抓；切歌/断开会话自动取消。</summary>
    private void ScheduleNotFoundRecovery(string key, string title, string artist)
    {
        CancelNotFoundRecovery();
        var cts = new CancellationTokenSource();
        _recoveryCts = cts;
        _ = RunNotFoundRecoveryAsync(key, title, artist, cts);
    }

    /// <summary>
    ///     "未找到歌词"恢复循环（只针对 NotFound，可能是临时失败）：
    ///     1.2s 快速重试（重新读最新时长）→ 仍失败接后台周期补抓（15s、最多 4 次）。
    ///     返回 Loaded → 无缝换上歌词；返回 Instrumental → 确认真没歌词，切"纯音乐"提示并停止。
    /// </summary>
    private async Task RunNotFoundRecoveryAsync(string key, string title, string artist, CancellationTokenSource cts)
    {
        try
        {
            var token = cts.Token;

            // 1. 快速重试：1.2s 后重新加载（此刻 EndTime 旧值问题通常已过）
            await Task.Delay(1200, token);
            token.ThrowIfCancellationRequested();
            if (key != _currentKey || _session is null)
                return;

            AppLog.Debug($"NotFound 快速重试: {title} / {artist}");
            var quickResult = await LoadLyricsOnceAsync(title, artist);
            if (key != _currentKey)
                return;

            if (quickResult != LyricsLoadResult.NotFound)
            {
                ApplyLoadResult(key, quickResult);
                return; // 成功 / 纯音乐：恢复完成
            }

            // 2. 快速重试也失败：正式显示"未找到歌词"，并启动后台周期补抓
            ApplyLoadResult(key, LyricsLoadResult.NotFound);
            var attempt = 0;
            while (attempt < NotFoundRefetchMaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(NotFoundRefetchIntervalSec), token);
                token.ThrowIfCancellationRequested();

                if (_session is null || key != _currentKey || _lyrics.Lines.Count > 0)
                    return; // 会话关闭 / 已切歌 / 歌词已就绪
                if (!IsActuallyPlaying(_session))
                    continue; // 暂停/停止：跳过本轮，恢复播放后继续

                var props = await _session.TryGetMediaPropertiesAsync();
                var pTitle = props.Title;
                var pArtist = props.Artist ?? string.Empty;
                if (string.IsNullOrWhiteSpace(pTitle))
                    continue; // 元数据仍没就绪，下一轮再试
                if (TrackKey.Of(pTitle, pArtist) != key)
                    return; // 实际已切歌

                attempt++;
                AppLog.Debug($"NotFound 后台补抓第 {attempt} 次: {pTitle} / {pArtist}");
                var result = await LoadLyricsOnceAsync(pTitle, pArtist);
                if (key != _currentKey)
                    return;
                if (result == LyricsLoadResult.NotFound)
                    continue; // 仍没找到，下一轮

                AppLog.Info($"NotFound 后台补抓得到确定结果: {result}");
                ApplyLoadResult(key, result);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 切歌/断开会话导致的正常取消
        }
        catch (Exception ex)
        {
            AppLog.Error($"NotFound 恢复调度异常: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_recoveryCts, cts))
                _recoveryCts = null;
        }
    }

    /// <summary>取消"未找到歌词"恢复调度（切歌/会话变化时调用）</summary>
    private void CancelNotFoundRecovery()
    {
        var cts = _recoveryCts;
        _recoveryCts = null;
        try
        {
            cts?.Cancel();
        }
        catch
        {
            // 已取消/已释放，忽略
        }
    }

    /// <summary>获取当前歌曲的歌词候选（托盘"歌词选择…"用）</summary>
    public async Task<IReadOnlyList<LyricCandidate>> GetLyricCandidatesAsync()
    {
        var session = _session;
        if (session is null || string.IsNullOrWhiteSpace(_currentTitle))
            return [];

        return await _lyrics.GetCandidatesAsync(_currentTitle, _currentArtist, ReadDurationMs());
    }

    /// <summary>应用用户选中的候选：取词 + 显示 + 记忆</summary>
    public async Task<bool> ApplyLyricCandidateAsync(LyricCandidate candidate)
    {
        var key = TrackKey.Of(_currentTitle, _currentArtist);
        // 取词需要网络请求（1~2 秒），先给歌词栏反馈，避免用户以为卡了
        StatusChanged?.Invoke("正在应用所选歌词…");

        var result = await _lyrics.LoadCandidateAsync(candidate);
        if (result == LyricsLoadResult.NotFound)
        {
            AppLog.Warn($"候选无歌词: '{candidate.Title}'（来源={candidate.SourceName}）");
            StatusChanged?.Invoke("所选歌词版本没有歌词，请换一个试试");
            return false;
        }

        // 选中的是纯音乐版本：正常显示"纯音乐"占位，但**不记忆**——
        // 否则误选纯音乐版会把"有歌词的歌"锁死成纯音乐，还得清缓存才能恢复。
        if (result == LyricsLoadResult.Instrumental)
        {
            AppLog.Warn($"候选为纯音乐: '{candidate.Title}'（来源={candidate.SourceName}）");
            ApplyLoadResult(key, result);
            return true;
        }

        _currentKey = key; // 确认当前歌曲，避免后续 key 校验丢弃
        LyricsChoiceService.Put(key, candidate); // 记忆选择
        ApplyLoadResult(key, result);             // 复用：重置切换状态 + 通知 UI
        AppLog.Info($"已应用用户选择的歌词: '{candidate.Title}' / {candidate.Artist}（{result}）");
        return true;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() => _ = RefreshTrackAsync());
    }

    /// <summary>
    ///     加载 SMTC 提供的歌曲封面；key 未变不重复加载，无封面/失败发 null 事件。
    /// </summary>
    private async Task RefreshCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props, string key)
    {
        if (!_mediaControlsEnabled)
            return;

        if (key != _coverKey)
        {
            _coverKey = key;
            _coverLoaded = false; // 新歌：封面待加载（QQ 会先推无封面、后推有封面）
        }
        if (_coverLoaded)
            return; // 同一首歌封面已成功加载，跳过

        try
        {
            if (props.Thumbnail is null)
            {
                // 本轮无封面：不置 loaded，等后续推送/轮询重试
                CoverChanged?.Invoke(key, null);
                return;
            }

            using var stream = await props.Thumbnail.OpenReadAsync();
            if (key != _coverKey)
                return; // 等待期间又切歌，丢弃过期封面

            // 一次性读入内存：SMTC 流可能不支持 Seek
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            ms.Position = 0;

            // WPF 的 BitmapImage/BitmapDecoder 解码 JPEG 时会读取内嵌 ICC 色彩配置，
            // 个别封面的 ICC 损坏会导致 IOException"无法从流中读取"。
            // 改用 GDI+（System.Drawing）解码：不读 ICC，容错后转成 WPF BitmapSource。
            using var gdiImage = System.Drawing.Image.FromStream(ms);
            using var scaled = new System.Drawing.Bitmap(gdiImage, new System.Drawing.Size(96, 96));
            var hBitmap = scaled.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(96, 96));
                source.Freeze();
                _coverLoaded = true;
                CoverChanged?.Invoke(key, source);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            if (key != _coverKey)
                return; // 过期失败不影响当前封面
            AppLog.Warn($"封面: 加载失败 key='{key}' [{ex.GetType().Name}] {ex.Message}");
            CoverChanged?.Invoke(key, null);
        }
    }

    /// <summary>媒体区开关：关闭时不再加载封面</summary>
    public void SetMediaControlsEnabled(bool enabled)
    {
        _mediaControlsEnabled = enabled;
        if (!enabled)
        {
            _coverKey = string.Empty;
            _coverLoaded = false;
            CoverChanged?.Invoke(string.Empty, null);
        }
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
        if (_session is null)
            return;
        try
        {
            await _session.TryTogglePlayPauseAsync();
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
        _switchSuspected = false;
        _switchingTrack = true;
        _switchKey = _currentKey;
        _switchStartedAt = Environment.TickCount;
        _lastSwitchStartUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        TrackSwitchStarted?.Invoke();
        StatusChanged?.Invoke("正在获取歌词…");
        AppLog.Debug($"切歌信号触发（{reason}），等待新歌 key='{_switchKey}'");
        PollForNewTrackAsync();
    }

    /// <summary>
    ///     播放位置大幅回退但时长未变：可能是真切歌（两首歌同时长），也可能是向后拖动进度条。
    ///     不立即清显示，先静默轮询确认歌曲 key 真的变化（且连续两次稳定）再切换。
    /// </summary>
    private void SuspectTrackSwitch()
    {
        if (_switchSuspected)
            return;
        _switchSuspected = true;
        AppLog.Debug("位置回退疑似切歌，等待确认（区分拖动进度条）");
        PollForTrackConfirmationAsync();
    }

    /// <summary>疑似切歌确认：连续两次读到同一个新 key 才算确认，之后走完整切换流程；否则静默作罢</summary>
    private async void PollForTrackConfirmationAsync()
    {
        try
        {
            string? candidateKey = null;
            for (var i = 0; i < 10; i++) // 最多 2 秒
            {
                await Task.Delay(200);
                if (!_switchSuspected || _session is null || _switchingTrack)
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
                    continue;
                }

                if (candidateKey == currentKey)
                {
                    // 同一个新 key 连续两次读到 → 确认切歌，走完整流程
                    AppLog.Debug("位置回退确认切歌，进入切换流程");
                    _switchSuspected = false;
                    await RefreshTrackAsync();
                    return;
                }

                candidateKey = currentKey;
            }
        }
        catch
        {
            // 确认失败不影响事件驱动路径
        }
        finally
        {
            _switchSuspected = false;
        }
    }

    /// <summary>
    ///     切歌早期信号已触发，但 MediaPropertiesChanged 事件可能滞后几百毫秒：
    ///     主动轮询媒体属性，歌名/歌手一变就立即开始搜歌词。
    /// </summary>
    private async void PollForNewTrackAsync()
    {
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(200);
                if (!_switchingTrack || _session is null)
                {
                    AppLog.Debug($"元数据轮询结束（i={i}），switching={_switchingTrack}");
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

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var session = _session;
            if (session is null)
                return;

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
            if (_lastEndTimeMs > 0 && endMs > 0 && Math.Abs(endMs - _lastEndTimeMs) > 500 &&
                !_switchingTrack && !RecentlySwitched())
                BeginTrackSwitch("时长变化");

            if (endMs > 0)
                _lastEndTimeMs = endMs;
        });
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var session = _session;
            if (session is null)
                return;

            GlobalSystemMediaTransportControlsSessionPlaybackStatus? status;
            try
            {
                status = session.GetPlaybackInfo().PlaybackStatus;
            }
            catch
            {
                return;
            }

            // SMTC 允许重复推送同一状态（QQ 切歌同一轮会推多次），只在状态真变化时处理
            if (status == _lastPlaybackStatus)
            {
                PlaybackStatusChanged?.Invoke(status);
                return;
            }
            _lastPlaybackStatus = status;

            // 从未播放/停止状态开始播放且当前没有歌词 → 主动刷新加载。
            // 播放开始时 SMTC 可能只推 PlaybackInfoChanged 而不推 MediaPropertiesChanged，
            // 不主动刷的话歌词永远不会加载（待机分支不进入切歌流程）。
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                _lyrics.Lines.Count == 0)
                _ = RefreshTrackAsync();

            // 切歌早期信号：旧歌停止（Stopped）→ 新歌加载（Changing），立即清旧歌词进入等待。
            // 拖动进度条只改 Position，不会触发 PlaybackInfoChanged，天然不会误判。
            if (!_switchingTrack && !RecentlySwitched() &&
                (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ||
                 status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing))
                BeginTrackSwitch($"播放状态 {status}");

            // 停止后恢复播放同一首歌（单曲循环/手动停止再播）：不是切歌，立即恢复正常显示
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                _switchingTrack && _currentKey == _switchKey && _lyrics.Lines.Count > 0)
            {
                AppLog.Debug("恢复播放同一首歌，取消等待状态");
                _switchingTrack = false;
                StatusChanged?.Invoke(string.Empty);
            }

            PlaybackStatusChanged?.Invoke(status);
        });
    }

    /// <summary>刚完成过切歌（2 秒内）：同一轮切歌序列的迟到信号应忽略</summary>
    public bool RecentlySwitched()
    {
        return _lastSwitchCompletedAt > 0 && Environment.TickCount - _lastSwitchCompletedAt < 2000;
    }

    /// <summary>
    ///     ticker 每拍上报原始播放位置：负责切歌检测（位置回退）、误判恢复与看门狗。
    ///     UI 不再直接维护切换状态。
    /// </summary>
    public void NotifyPlaybackPosition(int rawPositionMs)
    {
        if (_session is null)
            return;

        // 播放中位置大幅回退（>2s）→ 疑似切歌；但拖动进度条也会回退，所以只进入"怀疑"状态
        var jumpedBack = _lastTickPositionMs >= 0 && rawPositionMs < _lastTickPositionMs - 2000;
        if (jumpedBack && !_switchingTrack)
            SuspectTrackSwitch();
        _lastTickPositionMs = rawPositionMs;

        if (!_switchingTrack)
            return;

        var elapsed = Environment.TickCount - _switchStartedAt;

        // 新歌已确认（key 已变化）→ 保持占位，等歌词加载完成；
        // 超过 8 秒仍未完成 → 看门狗强制复位，后台加载完成会自行刷新。
        if (_currentKey != _switchKey)
        {
            if (elapsed > 8000)
            {
                AppLog.Warn("看门狗：歌词加载超时 8s，复位切换状态");
                _switchingTrack = false;
                if (_lyrics.Lines.Count == 0)
                    StatusChanged?.Invoke(string.Empty); // 清掉"正在获取歌词…"占位
            }
            return;
        }

        // 3 秒内没有新歌确认 → 大概率是拖动进度条误判，恢复正常显示
        if (elapsed > 3000)
        {
            AppLog.Debug("误判恢复：3s 内未确认新歌，恢复正常显示");
            _switchingTrack = false;
            if (_lyrics.Lines.Count == 0)
                StatusChanged?.Invoke(string.Empty); // 清掉"正在获取歌词…"占位
        }
    }

    /// <summary>读取当前时间线（失败返回 null）</summary>
    public GlobalSystemMediaTransportControlsSessionTimelineProperties? GetTimeline()
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

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
