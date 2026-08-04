using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Media.Control;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluentLyrics.Models;
using FluentLyrics.Services;
using FluentLyrics.Services.Lyrics;
using FluentLyrics.Services.Taskbar;

namespace FluentLyrics;

public partial class MainWindow
{
    // ---- Win32 窗口样式常量 ----
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000; // 不抢焦点
    private const long WS_EX_TRANSPARENT = 0x00000020; // 鼠标点击穿透
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const double MarqueeEndPadding = 24; // 滚到最右侧时预留的空白

    // ---- 运行时设置（来自 AppSettings）----
    private AppSettings _settings = AppSettingsService.Load();
    private double _marqueeSpeed = 55;   // 跑马灯速度（DIP/秒）
    private bool _clickThrough = true;   // 鼠标点击穿透
    private Color _lyricColor = Colors.White;
    private Color _lyricDimColor = Color.FromArgb(110, 255, 255, 255);
    private Brush _lyricBrush = Brushes.White;                    // 状态文本/亮色层画刷
    private Brush _lyricDimBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)); // 暗色层画刷
    private double _lastProgress = -1;                            // 上次渲染的进度（0~1）
    private int _lastTickPositionMs = -1;                         // 上一拍播放位置，用于切歌检测
    private double _lastEndTimeMs = -1;                           // 上次歌曲时长（切歌早期信号）
    private bool _switchingTrack;                                 // 正在切歌/等待新歌词
    private string _switchKey = string.Empty;                     // 触发切歌检测时的歌曲 key
    private int _switchStartedAt;                                 // 切歌检测触发时间（误判恢复用）

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ---- 状态 ----
    // 管理器必须一直持有：它是局部变量被 GC 回收后，会话事件（PlaybackInfoChanged 等）会静默丢失
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly LyricsService _lyrics = new();
    private readonly TaskbarService _taskbar = new();
    private readonly TrayIconService _tray = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private string _currentKey = string.Empty;
    private int _currentIndex = -1;
    private bool _hiddenForPlayback;

    public MainWindow()
    {
        InitializeComponent();
        _marqueeSpeed = _settings.MarqueeSpeed;
        _clickThrough = _settings.ClickThrough;
        ApplyLyricBrushes();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => PositionCard();
        _ticker.Tick += OnTickerTick;
        LyricHost.SizeChanged += (_, _) => ReapplyLyricDisplay();
        _taskbar.TaskbarChanged += PositionCard;

        // 托盘：显示/隐藏、退出（Ctrl+Alt+Q 与 QQ 音乐快捷键冲突，已移除，退出走托盘菜单）
        _tray.ShowHideRequested += ToggleCardVisibility;
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += Close;

        // 兜底：2 秒后无论如何显示，避免定位失败时窗口一直隐藏
        var safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        safetyTimer.Tick += (_, _) =>
        {
            safetyTimer.Stop();
            PositionCard(); // 重新定位一次：定位成功自己显示，组件隐藏则保持隐藏
            if (Visibility != Visibility.Visible && !_taskbar.HasTaskbar)
                Visibility = Visibility.Visible; // 任务栏还没就绪才兜底显示
        };

        safetyTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
        PositionCard();
    }

    /// <summary>置顶、不抢焦点；按设置开启鼠标点击穿透</summary>
    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return; // 窗口句柄未就绪（设置保存可能在初始化前触发重应用）

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_NOACTIVATE;
        if (_clickThrough)
            exStyle |= WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // 卡片右缘与开始按钮的间距（DIP）
    private const double CardRightGap = 8;
    private const double ComponentGap = 6;

    // 歌词文本在卡片内的起始 x（卡片紧贴组件右侧，从 0 开始即可）
    private readonly double _textBaseLeft = 0;

    private void PositionCard()
    {
        if (!_taskbar.HasTaskbar)
            return;

        // 暂停/停止/会话关闭时隐藏卡片（观感与 FluentFlyout 暂停消失一致）
        if (ShouldHideForPlayback())
        {
            if (Visibility != Visibility.Collapsed)
                Visibility = Visibility.Collapsed;
            return;
        }

        var taskbarRect = _taskbar.GetTaskbarRectDips(this);
        var widget = _taskbar.GetWidgetRectDips(this);
        var start = _taskbar.GetStartButtonRectDips(this);

        // 左边缘：组件右边缘（不覆盖组件，避免叠层闪烁）
        double left = widget is { } w
            ? w.Right + ComponentGap
            : taskbarRect.Left;

        // 右边缘：开始按钮左侧；找不到时主屏回退任务栏中线（开始按钮居中），副屏回退右缘留出托盘空间
        double rightEdge = start is { } s
            ? s.Left - CardRightGap
            : _taskbar.IsPrimaryTaskbar
                ? taskbarRect.Left + taskbarRect.Width / 2
                : taskbarRect.Right - 170;

        var cardWidth = Math.Max(120, Math.Min(rightEdge - left, taskbarRect.Width - left));
        var cardHeight = Math.Max(taskbarRect.Height, 36);

        Debug.WriteLine($"[FluentLyrics] 卡片 {left:F0}..{rightEdge:F0} 宽={cardWidth:F0}");

        // 位置/尺寸没变化就不动窗口：反复 SetWindowPos 会让分层窗口重绘，引起闪烁
        if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - taskbarRect.Top) > 0.5 ||
            Math.Abs(Width - cardWidth) > 0.5 || Math.Abs(Height - cardHeight) > 0.5)
        {
            Left = left;
            Top = taskbarRect.Top;
            Width = cardWidth;
            Height = cardHeight;
        }

        ShowCardIfReady();
        ReapplyLyricDisplay();
    }

    /// <summary>是否因播放状态而需要隐藏卡片；无会话或查询失败时不隐藏</summary>
    private bool ShouldHideForPlayback()
    {
        if (_session is null)
            return false;

        try
        {
            var status = _session.GetPlaybackInfo().PlaybackStatus;
            return status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        }
        catch
        {
            return false; // 查询失败时保持现状，避免误隐藏
        }
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(PositionCard);
    }

    /// <summary>托盘菜单：切换卡片显隐（暂停播放时仍保持隐藏，与播放状态逻辑一致）</summary>
    private void ToggleCardVisibility()
    {
        if (Visibility == Visibility.Visible)
        {
            Visibility = Visibility.Collapsed;
        }
        else
        {
            PositionCard();
            Visibility = Visibility.Visible;
        }
    }

    /// <summary>托盘菜单：打开设置窗口；保存后重新应用设置</summary>
    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        window.Saved += () =>
        {
            _settings = AppSettingsService.Load();
            ApplySettings();
        };
        window.ShowDialog();
    }

    /// <summary>保存设置后重应用：字号/颜色/滚动速度/点击穿透</summary>
    private void ApplySettings()
    {
        _marqueeSpeed = _settings.MarqueeSpeed;
        _clickThrough = _settings.ClickThrough;
        ApplyLyricBrushes();
        LyricTextBright.FontSize = _settings.FontSize;
        LyricTextDim.FontSize = _settings.FontSize;
        LyricTranslation.FontSize = Math.Max(10, _settings.FontSize - 3);
        LyricTextBright.Foreground = _lyricBrush;
        LyricTextDim.Foreground = _lyricDimBrush;
        ApplyWindowStyles(); // 更新点击穿透需要重设窗口扩展样式
        _currentIndex = -1;
        _lastProgress = -1;
        ReapplyLyricDisplay(); // 下一拍（100ms 内）会用新颜色重绘当前行
    }

    /// <summary>根据设置颜色生成亮/暗两色（供双层文本与状态文本使用）</summary>
    private void ApplyLyricBrushes()
    {
        var color = ParseColorValue(_settings.LyricColor);
        _lyricColor = color;
        _lyricDimColor = Color.FromArgb(110, color.R, color.G, color.B);
        _lyricBrush = new SolidColorBrush(_lyricColor);
        _lyricBrush.Freeze();
        _lyricDimBrush = new SolidColorBrush(_lyricDimColor);
        _lyricDimBrush.Freeze();
    }

    private static Color ParseColorValue(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.White;
        }
    }

    /// <summary>定位成功后再显示，避免启动瞬间以默认位置盖住组件</summary>
    private void ShowCardIfReady()
    {
        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(() => UpdateSession(_sessionManager.GetCurrentSession()));
            UpdateSession(_sessionManager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            SetStatus($"SMTC 初始化失败：{ex.Message}");
        }
    }

    private void UpdateSession(GlobalSystemMediaTransportControlsSession? session)
    {
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

        if (_session is null)
        {
            _lyrics.Clear();
            _currentKey = string.Empty;
            _currentIndex = -1;
            _ticker.Stop();
            SetStatus("未检测到播放会话。请先播放 QQ 音乐（需在设置中开启 SMTC）。");
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;

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

        PositionCard(); // 新会话接入后立即按播放状态刷新一次显隐

        _ticker.Start();
        _ = RefreshTrackAsync();
    }

    /// <summary>切歌时读取歌曲信息，若和上次不同则重新搜索歌词</summary>
    private async Task RefreshTrackAsync()
    {
        var session = _session;
        if (session is null)
            return;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var title = string.IsNullOrWhiteSpace(props.Title) ? "未知歌曲" : props.Title;
            var artist = string.IsNullOrWhiteSpace(props.Artist) ? "未知歌手" : props.Artist;

            var key = $"{title}\u0001{artist}";
            if (key == _currentKey)
                return;

            _switchingTrack = true;
            _currentKey = key;
            _currentIndex = -1;
            _lastProgress = -1;
            SetStatus("正在获取歌词…");

            // 时长尽量带上，能提高 QQ 搜索的匹配准确度；拿不到也没关系
            int? durationMs = null;
            try
            {
                var endTime = session.GetTimelineProperties().EndTime;
                if (endTime > TimeSpan.Zero)
                    durationMs = (int)endTime.TotalMilliseconds;
            }
            catch
            {
                // 某些播放器不提供时长，忽略
            }

            var result = await _lyrics.LoadAsync(title, artist, durationMs);

            // 等待期间可能已经切到下一首歌，旧结果直接丢弃
            if (key != _currentKey)
                return;

            _switchingTrack = false;
            _currentIndex = -1;
            _lastProgress = -1;
            SetStatus(result switch
            {
                LyricsLoadResult.Loaded => string.Empty,
                LyricsLoadResult.Instrumental => "此歌曲为没有填词的纯音乐，请您欣赏",
                _ => "未找到歌词",
            });
        }
        catch (Exception ex)
        {
            SetStatus($"读取媒体信息失败：{ex.Message}");
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(() => _ = RefreshTrackAsync());
    }

    /// <summary>进入"等待新歌"状态：清显示、记录触发点，并主动轮询媒体属性（MediaPropertiesChanged 可能滞后）</summary>
    private void BeginTrackSwitch()
    {
        _switchingTrack = true;
        _switchKey = _currentKey;
        _switchStartedAt = Environment.TickCount;
        _currentIndex = -1;
        _lastProgress = -1;
        SetStatus("正在获取歌词…");
        PollForNewTrackAsync();
    }

    /// <summary>
    /// 切歌早期信号已触发，但 MediaPropertiesChanged 事件可能滞后几百毫秒：
    /// 主动轮询媒体属性，歌名/歌手一变就立即开始搜歌词。key 未变时 RefreshTrackAsync 会直接返回。
    /// </summary>
    private async void PollForNewTrackAsync()
    {
        try
        {
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(200);
                if (!_switchingTrack || _currentKey != _switchKey || _session is null)
                    return; // 新歌已确认 / 误判已恢复 / 会话关闭
                await RefreshTrackAsync();
            }
        }
        catch
        {
            // 轮询失败不影响事件驱动路径
        }
    }

    /// <summary>
    /// 切歌时 SMTC 几乎必然先推送新的时长（不同歌曲时长基本不同），
    /// 比"位置回退"信号到得更早：时长一变立即清掉旧歌词进入等待状态，由 RefreshTrackAsync 确认。
    /// 拖动进度条不会改变时长，不会误触发；时长偶发为 0 时不更新缓存也不触发。
    /// </summary>
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
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

            // 前后都有有效时长且明显不同 → 切歌早期信号
            if (_lastEndTimeMs > 0 && endMs > 0 && Math.Abs(endMs - _lastEndTimeMs) > 500 && !_switchingTrack)
                BeginTrackSwitch();

            if (endMs > 0)
                _lastEndTimeMs = endMs;
        });
    }

    /// <summary>
    /// 定时器：每拍检查播放状态驱动显隐；检测切歌（播放中位置大幅回退到开头）时立即
    /// 清掉旧歌词显示"正在获取歌词…"，避免新歌开头还闪上一首歌的歌词。
    /// </summary>
    private void OnTickerTick(object? sender, EventArgs e)
    {
        var session = _session;
        if (session is null)
            return;

        // 播放状态变化 → 立即刷新卡片显隐（PlaybackInfoChanged 事件之外的 100ms 兜底）
        var hide = ShouldHideForPlayback();
        if (hide != _hiddenForPlayback)
        {
            _hiddenForPlayback = hide;
            PositionCard();
        }

        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline;
        int rawPositionMs;
        int positionMs;
        try
        {
            timeline = session.GetTimelineProperties();
            rawPositionMs = (int)timeline.Position.TotalMilliseconds;
            positionMs = GetPositionMs(timeline, session);
        }
        catch
        {
            return;
        }

        // 切歌检测（用原始位置，避免 LastUpdatedTime 滞后把位置外推虚高而漏检）：
        // 信号1：播放中位置大幅回退（>2s）→ 典型切歌行为。
        // 注意：不要按"位置超出歌词末尾"判切歌——尾奏（最后一句唱完到歌曲结束）期间
        // 位置必然超出歌词末尾，会误判；"切歌但位置未回退"由 EndTime 变化信号覆盖。
        var jumpedBack = _lastTickPositionMs >= 0 && rawPositionMs < _lastTickPositionMs - 2000;

        // 已在等待新歌时不重复进入：否则每拍都重置恢复计时，误判兜底永远触发不了
        if (jumpedBack && !_switchingTrack)
        {
            BeginTrackSwitch();
        }
        _lastTickPositionMs = rawPositionMs;

        if (_switchingTrack)
        {
            // 新歌已确认（key 已变化）→ 保持占位，等歌词加载完成
            if (_currentKey != _switchKey)
                return;

            // 3 秒内没有新歌确认 → 大概率是拖动进度条误判，恢复正常显示
            if (Environment.TickCount - _switchStartedAt > 3000)
            {
                _switchingTrack = false;
                _currentIndex = -1;
                _lastProgress = -1;
            }
            return;
        }

        if (_lyrics.Lines.Count == 0)
            return;

        int index;
        try
        {
            index = _lyrics.GetLineIndexAt(positionMs);
        }
        catch
        {
            return;
        }

        if (index == _currentIndex)
        {
            // 同一行：只平滑推进渐变边界
            if (index < 0)
                return;

            UpdateLyricProgress(positionMs);
            return;
        }

        _currentIndex = index;
        _lastProgress = -1;

        if (index < 0)
        {
            SetStatus(string.Empty);
            return;
        }

        ShowLyricLine(_lyrics.Lines[index], positionMs);
    }

    /// <summary>渲染当前歌词行：暗色全文垫底，亮色层用 Clip 按进度从左揭开</summary>
    private void ShowLyricLine(LyricLine line, int positionMs)
    {
        LyricTextDim.Text = line.Text;
        LyricTextDim.Foreground = _lyricDimBrush;
        LyricTextBright.Text = line.Text;
        LyricTextBright.Foreground = _lyricBrush;

        // 非中文歌词且有翻译 → 双行显示；中文歌/无翻译保持单行
        var translation = ShouldShowTranslation(line) ? line.Translation : null;
        LyricTranslation.Text = translation ?? string.Empty;
        LyricTranslation.Visibility = string.IsNullOrEmpty(translation)
            ? Visibility.Collapsed
            : Visibility.Visible;

        _lastProgress = -1;
        LyricTextBright.Clip = null; // 清掉上一行的裁剪，避免行时长缺失时残留旧边界
        ReapplyLyricDisplay(); // 先完成布局，UpdateLyricProgress 需要文本实际宽度
        UpdateLyricProgress(positionMs);
    }

    /// <summary>
    /// 是否显示翻译：数据驱动——QQ 提供了译文且译文与正词不同才显示。
    /// 不猜歌词语言：中文歌 QQ 一般不返回译文（或返回相同文本），会自动跳过；任何语言通用。
    /// </summary>
    private static bool ShouldShowTranslation(LyricLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Translation))
            return false;

        return !string.Equals(line.Translation.Trim(), line.Text.Trim(), StringComparison.Ordinal);
    }

    /// <summary>按连续进度右移亮色层的 Clip 右缘（0~1），只在明显变化时更新</summary>
    private void UpdateLyricProgress(int positionMs)
    {
        if (_currentIndex < 0 || _currentIndex >= _lyrics.Lines.Count)
            return;

        var line = _lyrics.Lines[_currentIndex];
        var duration = GetLineDuration(_currentIndex);
        if (duration <= 0)
            return;

        var progress = (positionMs - line.StartTime) / (double)duration;
        progress = Math.Clamp(progress, 0, 1);
        if (Math.Abs(progress - _lastProgress) < 0.002)
            return;

        _lastProgress = progress;
        var width = LyricTextBright.ActualWidth;
        if (width <= 0)
            return;

        // 亮色层只露出 0..progress 部分：一条干净竖直边界扫过整行，不受字形段拆分影响
        LyricTextBright.Clip = new RectangleGeometry(
            new Rect(0, 0, width * progress, LyricTextBright.ActualHeight));
    }

    /// <summary>行的持续时间：优先取 EndTime，缺省时用下一行开始时间兜底</summary>
    private int GetLineDuration(int index)
    {
        var lines = _lyrics.Lines;
        if (index < 0 || index >= lines.Count)
            return 0;

        var line = lines[index];
        if (line.EndTime is { } end)
            return end - line.StartTime;

        return index + 1 < lines.Count
            ? lines[index + 1].StartTime - line.StartTime
            : 0;
    }

    /// <summary>
    /// 计算当前播放位置（毫秒）。
    /// SMTC 的 Position 是"上次更新时刻"的快照，QQ 音乐约每秒才推送一次，
    /// 直接使用会导致歌词切换滞后；这里用 LastUpdatedTime 外推到当前时刻。
    /// </summary>
    private int GetPositionMs(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        GlobalSystemMediaTransportControlsSession session)
    {
        var position = timeline.Position;

        // 仅在播放中做外推；暂停/停止时直接用上报的位置
        if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(2))
            {
                position += elapsed;
            }
        }

        // 全局歌词偏移：正数 = 歌词提前显示
        position += TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        return (int)position.TotalMilliseconds;
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => UpdateLyricDisplay(text));
    }

    /// <summary>显示歌词/状态文本：短文本贴左（紧挨组件卡片），超长文本跑马灯滚动</summary>
    private void UpdateLyricDisplay(string text)
    {
        // 状态文本只走亮色层（无进度裁剪），暗色层清空避免残留上一行歌词的"鬼影"
        LyricTextDim.Text = string.Empty;
        LyricTextBright.Text = text;
        LyricTextBright.Foreground = _lyricBrush;
        LyricTextBright.Clip = null;
        LyricTranslation.Text = string.Empty;
        LyricTranslation.Visibility = Visibility.Collapsed;
        ReapplyLyricDisplay();
    }

    private void ReapplyLyricDisplay()
    {
        if (LyricHost.ActualWidth <= 0)
            return;

        LyricStack.UpdateLayout();
        var hostWidth = LyricHost.ActualWidth;
        var hostHeight = LyricHost.ActualHeight;
        var textWidth = LyricStack.ActualWidth;
        var availableWidth = Math.Max(0, hostWidth - _textBaseLeft);

        // 垂直居中
        Canvas.SetTop(LyricStack, Math.Max(0, (hostHeight - LyricStack.ActualHeight) / 2));

        // 短文本：紧贴卡片左缘（挨着组件）静态显示
        if (textWidth <= availableWidth - 8)
        {
            LyricStack.BeginAnimation(TranslateTransform.XProperty, null);
            Canvas.SetLeft(LyricStack, _textBaseLeft);
            LyricTranslate.X = 0;
            return;
        }

        // 超长文本：跑马灯（从组件右侧开始，向左滚出再返回）
        Canvas.SetLeft(LyricStack, _textBaseLeft);
        var distance = textWidth - availableWidth + MarqueeEndPadding;
        if (distance <= 0)
        {
            LyricStack.BeginAnimation(TranslateTransform.XProperty, null);
            LyricTranslate.X = 0;
            return;
        }

        var duration = TimeSpan.FromSeconds(Math.Max(4, distance / _marqueeSpeed));
        var animation = new DoubleAnimation(0, -distance, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Timeline.SetDesiredFrameRate(animation, 30); // 降低分层窗口重绘频率，减轻 DWM 合成压力
        LyricTranslate.X = 0;
        LyricStack.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray.Dispose();
        _taskbar.Dispose();
        base.OnClosed(e);
    }
}
