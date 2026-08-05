using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Media.Control;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _switchSuspected;                                // 位置回退疑似切歌，等待 key 确认（区分拖动进度条）
    private bool _lyricsRetryPending;                             // 是否已有"未找到歌词"延迟重试在跑
    private string _marqueeSignature = string.Empty;              // 上次跑马灯签名（文本+距离+时长），避免重复重启
    private string _reapplyDiagSig = string.Empty;                // 布局诊断签名（仅变化时打印，防刷屏）
    private bool _adjustingPosition;                              // 托盘"调整歌词位置"模式（临时关闭点击穿透）
    private bool _dragging;                                       // 调整模式下正在拖动卡片
    private Point _dragGrabOffset;                                // 光标(屏幕DIP)相对窗口左上角的抓取偏移
    private double _lastClipWidth = -1;                           // 上次裁剪宽度（逐字/线性共用去重）
    private readonly Dictionary<string, double[]> _wordBoundaryCache = new(); // 行文本 → 各字结束位置的像素宽度

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

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
        _taskbar.ApplySettings(_settings);

        // 托盘：显示/隐藏、设置、打开日志目录、退出（Ctrl+Alt+Q 与 QQ 音乐快捷键冲突，已移除）
        _tray.ShowHideRequested += ToggleCardVisibility;
        _tray.SettingsRequested += OpenSettings;
        _tray.OpenLogDirectoryRequested += OpenLogDirectory;
        _tray.ClearCacheRequested += _lyrics.ClearCache;
        _tray.AdjustPositionRequested += ToggleAdjustPosition;
        _tray.ResetPositionRequested += ResetManualOffset;
        _tray.ExitRequested += Close;

        // 拖动调整位置：仅"调整模式"下窗口能收到鼠标（平时点击穿透）
        MouseLeftButtonDown += OnAdjustMouseDown;
        MouseMove += OnAdjustMouseMove;
        MouseLeftButtonUp += OnAdjustMouseUp;
        MouseRightButtonDown += OnAdjustMouseRightDown;

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
        else
            exStyle &= ~WS_EX_TRANSPARENT; // 调整位置模式需要接收鼠标，必须清掉点击穿透位
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

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

        // 调整模式下位置由用户拖动控制，不自动定位（退出时再恢复锚点+偏移）
        if (_adjustingPosition)
            return;

        var taskbarRect = _taskbar.GetTaskbarRectDips(this);
        if (_taskbar.GetCardRegionDips(this) is not { } card)
            return; // 还没算出来，等下一轮定位

        var cardWidth = _settings.AutoCardWidth
            ? Math.Max(120, card.Width)
            : Math.Max(120, _settings.CardWidth);
        cardWidth = Math.Min(cardWidth, taskbarRect.Width); // 不超出任务栏
        var cardHeight = Math.Max(taskbarRect.Height, 36);

        // 卡片必须待在任务栏范围内：手动偏移或锚点变化都不应把它拖出任务栏
        var left = Math.Clamp(card.Left + _settings.ManualOffsetX,
            taskbarRect.Left, Math.Max(taskbarRect.Left, taskbarRect.Right - cardWidth));
        var top = Math.Clamp(card.Top + _settings.ManualOffsetY,
            taskbarRect.Top, Math.Max(taskbarRect.Top, taskbarRect.Bottom - cardHeight));

        AppLog.Debug($"卡片 {left:F0}..{left + cardWidth:F0} 宽={cardWidth:F0} " +
                     $"偏移=({_settings.ManualOffsetX:F0},{_settings.ManualOffsetY:F0})");

        // 位置/尺寸没变化就不动窗口：反复 SetWindowPos 会让分层窗口重绘，引起闪烁
        if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5 ||
            Math.Abs(Width - cardWidth) > 0.5 || Math.Abs(Height - cardHeight) > 0.5)
        {
            Left = left;
            Top = top;
            Width = cardWidth;
            Height = cardHeight;
            ReapplyLyricDisplay(); // 尺寸变化才重新应用布局；无变化时保持跑马灯继续滚动，不打断
        }

        ShowCardIfReady();
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

    /// <summary>托盘菜单：用资源管理器打开日志目录，方便反馈问题时直接拷日志</summary>
    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            Process.Start(new ProcessStartInfo(AppLog.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("打开日志目录失败", ex);
        }
    }

    /// <summary>托盘菜单：进入/退出"调整位置"模式（进入时临时关闭点击穿透以便拖拽）</summary>
    private void ToggleAdjustPosition()
    {
        if (_adjustingPosition)
        {
            ExitAdjustMode(save: true); // 再点一次 = 保存并退出
            return;
        }

        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;

        _adjustingPosition = true;
        _clickThrough = false;
        _currentIndex = -1;
        _lastProgress = -1;
        ApplyWindowStyles();
        _tray.SetAdjusting(true);
        SetStatus("拖动卡片调整位置，右键完成");
        AppLog.Info("进入位置调整模式（点击穿透已临时关闭）");
    }

    /// <summary>托盘菜单：清除手动偏移，回到自动定位</summary>
    private void ResetManualOffset()
    {
        if (_adjustingPosition)
            ExitAdjustMode(save: false);

        _settings.ManualOffsetX = 0;
        _settings.ManualOffsetY = 0;
        AppSettingsService.Save(_settings);
        AppLog.Info("已重置手动位置偏移");
        PositionCard();
    }

    /// <summary>退出调整模式：save=true 先把当前偏移存盘；false 仅退出（供"重置位置"复用）</summary>
    private void ExitAdjustMode(bool save)
    {
        if (!_adjustingPosition)
            return;

        if (save)
            SaveManualOffset();

        _adjustingPosition = false;
        if (_dragging)
        {
            ReleaseMouseCapture();
            _dragging = false;
        }

        _clickThrough = _settings.ClickThrough;
        ApplyWindowStyles();
        _tray.SetAdjusting(false);
        _currentIndex = -1;
        _lastProgress = -1;
        SetStatus(string.Empty); // 清掉调整提示，下一拍 ticker 会重绘当前行
        AppLog.Info("退出位置调整模式");
        PositionCard(); // 恢复锚点+偏移定位
    }

    private void OnAdjustMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_adjustingPosition)
            return;

        _dragging = true;
        var cursor = GetCursorPosDips();
        _dragGrabOffset = new Point(Left - cursor.X, Top - cursor.Y);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnAdjustMouseMove(object sender, MouseEventArgs e)
    {
        if (!_adjustingPosition || !_dragging)
            return;

        // 用绝对光标位置 + 抓取偏移定位：窗口移动不会产生"相对坐标反馈"导致抖动/不跟手
        var cursor = GetCursorPosDips();
        var taskbar = _taskbar.GetTaskbarRectDips(this);
        var maxLeft = Math.Max(taskbar.Left, taskbar.Right - Width);
        var maxTop = Math.Max(taskbar.Top, taskbar.Bottom - Height);
        Left = Math.Clamp(cursor.X + _dragGrabOffset.X, taskbar.Left, maxLeft);
        Top = Math.Clamp(cursor.Y + _dragGrabOffset.Y, taskbar.Top, maxTop);
        e.Handled = true;
    }

    private void OnAdjustMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_adjustingPosition || !_dragging)
            return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;

        // 松手保存当前偏移，但保持调整模式，方便继续微调；右键/托盘菜单再保存退出
        SaveManualOffset();
    }

    /// <summary>把当前窗口位置换算成相对自动锚点区域的偏移并保存（DIP）</summary>
    private void SaveManualOffset()
    {
        if (_taskbar.GetCardRegionDips(this) is { } anchor)
        {
            _settings.ManualOffsetX = Left - anchor.Left;
            _settings.ManualOffsetY = Top - anchor.Top;
        }
        AppSettingsService.Save(_settings);
        AppLog.Info($"手动偏移已保存: X={_settings.ManualOffsetX:F1}, Y={_settings.ManualOffsetY:F1}");
    }

    /// <summary>光标屏幕位置（物理像素）换算成 DIP，供拖动定位使用</summary>
    private Point GetCursorPosDips()
    {
        GetCursorPos(out var pt);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        return transform.Transform(new Point(pt.X, pt.Y));
    }

    private void OnAdjustMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!_adjustingPosition)
            return;

        e.Handled = true;
        ExitAdjustMode(save: true); // 右键 = 保存并退出
    }

    /// <summary>保存设置后重应用：字号/颜色/滚动速度/点击穿透</summary>
    private void ApplySettings()
    {
        _taskbar.ApplySettings(_settings);
        _marqueeSpeed = _settings.MarqueeSpeed;
        _clickThrough = _settings.ClickThrough;
        ApplyLyricBrushes();
        _wordBoundaryCache.Clear(); // 字号可能变化，逐字宽度缓存作废
        _lastClipWidth = -1;
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
            var hasSession = _sessionManager.GetCurrentSession() is not null;
            AppLog.Info($"SMTC 初始化成功（当前会话: {(hasSession ? "有" : "无")}）");
            UpdateSession(_sessionManager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            AppLog.Error($"SMTC 初始化失败: {ex.Message}");
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
        _switchSuspected = false;

        if (_session is null)
        {
            AppLog.Info("SMTC 会话断开");
            _lyrics.Clear();
            _currentKey = string.Empty;
            _currentIndex = -1;
            _ticker.Stop();
            SetStatus("未检测到播放会话。请先播放 QQ 音乐（需在设置中开启 SMTC）。");
            return;
        }

        AppLog.Info("SMTC 会话接入");
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

            // 切歌瞬间 SMTC 元数据可能短暂为空：不能把空值当成"未知歌曲"去搜索，
            // 否则会污染当前歌曲 key，并让轮询误以为"新歌已确认"而提前终止。
            // 这里只等待，不更新任何状态，由轮询/后续事件重读。
            if (string.IsNullOrWhiteSpace(props.Title))
            {
                AppLog.Debug("元数据未就绪（空标题），等待轮询重读");
                if (!_switchingTrack)
                    BeginTrackSwitch("元数据未就绪"); // 顺带启动元数据轮询，等真实歌名出现
                return;
            }

            var title = props.Title;
            var artist = props.Artist ?? string.Empty;

            var key = $"{title}\u0001{artist}";
            if (key == _currentKey)
                return;

            _switchingTrack = true;
            _currentKey = key;
            _currentIndex = -1;
            _lastProgress = -1;
            SetStatus("正在获取歌词…");
            AppLog.Info($"开始加载歌词: {title} / {artist}");

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

            AppLog.Info($"歌词加载完成: {result}");
            _switchingTrack = false;
            _currentIndex = -1;
            _lastProgress = -1;
            SetStatus(StatusTextFor(result));

            // 偶发：切歌瞬间搜索失败 / QQ 接口限流。NotFound 不缓存，延迟重试一次，
            // 避免"未找到歌词"被定死整首歌（重试期间已有占位文本，无闪烁）。
            if (result == LyricsLoadResult.NotFound)
                RetryLoadLyricsAsync(key, title, artist, durationMs);
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取媒体信息失败: {ex.Message}");
            SetStatus($"读取媒体信息失败：{ex.Message}");
        }
    }

    /// <summary>歌词加载结果的占位文案</summary>
    private static string StatusTextFor(LyricsLoadResult result) => result switch
    {
        LyricsLoadResult.Loaded => string.Empty,
        LyricsLoadResult.Instrumental => "此歌曲为没有填词的纯音乐，请您欣赏",
        _ => "未找到歌词",
    };

    /// <summary>
    /// "未找到歌词"的延迟重试：切歌瞬间 QQ 搜索偶发失败（元数据刚就绪 / 接口限流），
    /// 1.2 秒后重试一次；期间保持"未找到歌词"占位，重试成功会直接刷新显示。
    /// </summary>
    private async void RetryLoadLyricsAsync(string key, string title, string artist, int? durationMs)
    {
        if (_lyricsRetryPending)
            return;
        _lyricsRetryPending = true;

        try
        {
            AppLog.Debug($"NotFound 延迟重试启动: {title} / {artist}");
            await Task.Delay(1200);
            if (key != _currentKey || _session is null)
                return; // 已切到别的歌 / 会话关闭，放弃本次重试

            var result = await _lyrics.LoadAsync(title, artist, durationMs);
            if (key != _currentKey)
                return; // 等待期间又切歌，旧结果丢弃

            AppLog.Debug($"NotFound 重试结果: {result}");
            _switchingTrack = false;
            _currentIndex = -1;
            _lastProgress = -1;
            SetStatus(StatusTextFor(result));
        }
        catch
        {
            // 重试失败保持现状（"未找到歌词"），不影响事件驱动路径
        }
        finally
        {
            _lyricsRetryPending = false;
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(() => _ = RefreshTrackAsync());
    }

    /// <summary>进入"等待新歌"状态：清显示、记录触发点，并主动轮询媒体属性（MediaPropertiesChanged 可能滞后）</summary>
    private void BeginTrackSwitch(string reason)
    {
        _switchSuspected = false;
        _switchingTrack = true;
        _switchKey = _currentKey;
        _switchStartedAt = Environment.TickCount;
        _currentIndex = -1;
        _lastProgress = -1;
        SetStatus("正在获取歌词…");
        AppLog.Debug($"切歌信号触发（{reason}），等待新歌 key='{_switchKey}'");
        PollForNewTrackAsync();
    }

    /// <summary>
    /// 播放位置大幅回退但时长未变：可能是真切歌（两首歌同时长），也可能是向后拖动进度条。
    /// 不立即清显示/显示"正在获取歌词…"，先静默轮询确认歌曲 key 真的变化（且连续两次稳定）再切换，
    /// 避免拖进度条时闪"正在获取歌词…"或触发多余的歌词请求。
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
                        currentKey = $"{props.Title}\u0001{props.Artist ?? string.Empty}";
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
    /// 切歌早期信号已触发，但 MediaPropertiesChanged 事件可能滞后几百毫秒：
    /// 主动轮询媒体属性，歌名/歌手一变就立即开始搜歌词。key 未变时 RefreshTrackAsync 会直接返回。
    /// </summary>
    private async void PollForNewTrackAsync()
    {
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(200);
                // 不再以"key 已变化"作为退出条件：空元数据会误报"新歌已确认"导致轮询提前终止。
                // 统一等到歌词加载完成（_switchingTrack 复位）或超时。
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
                BeginTrackSwitch("时长变化");

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

        // 调整位置模式下不渲染歌词进度，避免卡拉OK裁剪盖住提示文字
        if (_adjustingPosition)
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
        // 信号1：播放中位置大幅回退（>2s）→ 典型切歌行为；
        // 但向后拖动进度条也会大幅回退，所以只进入"怀疑"状态，确认 key 变了才真正切歌。
        // 注意：不要按"位置超出歌词末尾"判切歌——尾奏（最后一句唱完到歌曲结束）期间
        // 位置必然超出歌词末尾，会误判；"切歌但位置未回退"由 EndTime 变化信号覆盖。
        var jumpedBack = _lastTickPositionMs >= 0 && rawPositionMs < _lastTickPositionMs - 2000;

        // 已在等待新歌时不重复进入：否则每拍都重置恢复计时，误判兜底永远触发不了
        if (jumpedBack && !_switchingTrack)
        {
            SuspectTrackSwitch();
        }
        _lastTickPositionMs = rawPositionMs;

        if (_switchingTrack)
        {
            var elapsed = Environment.TickCount - _switchStartedAt;

            // 新歌已确认（key 已变化）→ 保持占位，等歌词加载完成；
            // 超过 8 秒仍未完成（QQ 接口偶发挂起）→ 看门狗强制复位，后台加载完成会自行刷新。
            if (_currentKey != _switchKey)
            {
                if (elapsed > 8000)
                {
                    AppLog.Warn("看门狗：歌词加载超时 8s，复位切换状态");
                    _switchingTrack = false;
                    _currentIndex = -1;
                    _lastProgress = -1;
                    if (_lyrics.Lines.Count == 0)
                        UpdateLyricDisplay(string.Empty); // 清掉"正在获取歌词…"占位
                }
                return;
            }

            // 3 秒内没有新歌确认 → 大概率是拖动进度条误判，恢复正常显示
            if (elapsed > 3000)
            {
                AppLog.Debug("误判恢复：3s 内未确认新歌，恢复正常显示");
                _switchingTrack = false;
                _currentIndex = -1;
                _lastProgress = -1;
                if (_lyrics.Lines.Count == 0)
                    UpdateLyricDisplay(string.Empty); // 清掉"正在获取歌词…"占位
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
        _lastClipWidth = -1;
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

        // QQ 翻译里的 "//" 占位（无翻译），不显示
        if (string.Equals(line.Translation.Trim(), "//", StringComparison.Ordinal))
            return false;

        return !string.Equals(line.Translation.Trim(), line.Text.Trim(), StringComparison.Ordinal);
    }

    /// <summary>按连续进度右移亮色层的 Clip 右缘（0~1），只在明显变化时更新</summary>
    private void UpdateLyricProgress(int positionMs)
    {
        if (_currentIndex < 0 || _currentIndex >= _lyrics.Lines.Count)
            return;

        var line = _lyrics.Lines[_currentIndex];
        var width = LyricTextBright.ActualWidth;
        if (width <= 0)
            return;

        double clipWidth;
        if (line.HasWordTiming && TryGetWordClipWidth(line, positionMs, out var wordClip))
        {
            clipWidth = wordClip; // 逐字：按演唱节奏点亮
        }
        else
        {
            var duration = GetLineDuration(_currentIndex);
            if (duration <= 0)
                return;

            var progress = Math.Clamp((positionMs - line.StartTime) / (double)duration, 0, 1);
            if (Math.Abs(progress - _lastProgress) < 0.002)
                return;
            _lastProgress = progress;
            clipWidth = width * progress; // 兜底：整行线性揭晓
        }

        if (Math.Abs(clipWidth - _lastClipWidth) < 0.5)
            return;
        _lastClipWidth = clipWidth;

        // 亮色层只露出 0..clipWidth 部分：一条干净竖直边界扫过整行，不受字形段拆分影响
        LyricTextBright.Clip = new RectangleGeometry(
            new Rect(0, 0, clipWidth, LyricTextBright.ActualHeight));
    }

    /// <summary>
    /// 逐字揭晓：找到当前演唱到的字（绝对时间二分），亮色边界 = 之前各字累计宽度 + 当前字内平滑进度。
    /// 逐字文本与正词对不上时返回 false，由调用方回退线性揭晓。
    /// </summary>
    private bool TryGetWordClipWidth(LyricLine line, int positionMs, out double clipWidth)
    {
        clipWidth = 0;
        var words = line.Words;
        var boundaries = GetWordBoundaries(line);
        if (words is null || boundaries is null || boundaries.Length != words.Count)
            return false;

        int lo = 0, hi = words.Count - 1, index = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (words[mid].StartTime <= positionMs)
            {
                index = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (index < 0)
            return true; // 还没唱到第一个字：不亮

        var word = words[index];
        var before = index > 0 ? boundaries[index - 1] : 0;
        var wordWidth = boundaries[index] - before;
        var fraction = word.EndTime > word.StartTime
            ? Math.Clamp((positionMs - word.StartTime) / (double)(word.EndTime - word.StartTime), 0, 1)
            : 1;
        clipWidth = before + fraction * wordWidth;
        return true;
    }

    /// <summary>
    /// 计算一行里每个逐字片段结束位置对应的像素宽度（缓存按行文本复用）。
    /// 逐字片段需能按顺序在正词里匹配到，否则返回 null 表示该行不适合逐字渲染。
    /// </summary>
    private double[]? GetWordBoundaries(LyricLine line)
    {
        var words = line.Words;
        if (words is null || words.Count == 0)
            return null;

        if (_wordBoundaryCache.TryGetValue(line.Text, out var cached))
            return cached;

        var endChar = new int[words.Count];
        var charPos = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var text = words[i].Text;
            var found = line.Text.IndexOf(text, charPos, StringComparison.Ordinal);
            if (found < 0)
                return null; // 逐字文本与正词对不上，该行回退线性揭晓
            charPos = found + text.Length;
            endChar[i] = charPos;
        }

        var boundaries = new double[words.Count];
        var typeface = new Typeface(
            LyricTextBright.FontFamily,
            LyricTextBright.FontStyle,
            LyricTextBright.FontWeight,
            LyricTextBright.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < words.Count; i++)
        {
            var prefix = line.Text.Substring(0, endChar[i]);
            var formatted = new FormattedText(
                prefix,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                LyricTextBright.FontSize,
                Brushes.Black,
                dpi);
            boundaries[i] = formatted.WidthIncludingTrailingWhitespace;
        }

        _wordBoundaryCache[line.Text] = boundaries;
        return boundaries;
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
        _lastClipWidth = -1;
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

        // 诊断：尺寸或文本变化才打印，避免刷屏；帮助确认跑马灯分支是否进入
        var diagSig = $"{hostWidth:F0}|{textWidth:F0}|{availableWidth:F0}|{LyricTextBright.Text}";
        if (diagSig != _reapplyDiagSig)
        {
            _reapplyDiagSig = diagSig;
            AppLog.Debug($"歌词布局: host={hostWidth:F0} text={textWidth:F0} avail={availableWidth:F0} 文本='{LyricTextBright.Text}'");
        }

        // 垂直居中
        Canvas.SetTop(LyricStack, Math.Max(0, (hostHeight - LyricStack.ActualHeight) / 2));

        // 短文本：紧贴卡片左缘（挨着组件）静态显示
        if (textWidth <= availableWidth - 8)
        {
            LyricStack.BeginAnimation(TranslateTransform.XProperty, null);
            Canvas.SetLeft(LyricStack, _textBaseLeft);
            LyricTranslate.X = 0;
            _marqueeSignature = string.Empty;
            return;
        }

        // 超长文本：跑马灯（从组件右侧开始，向左滚出再返回）
        Canvas.SetLeft(LyricStack, _textBaseLeft);
        var distance = textWidth - availableWidth + MarqueeEndPadding;
        if (distance <= 0)
        {
            LyricStack.BeginAnimation(TranslateTransform.XProperty, null);
            LyricTranslate.X = 0;
            _marqueeSignature = string.Empty;
            return;
        }

        var duration = TimeSpan.FromSeconds(Math.Max(4, distance / Math.Max(_marqueeSpeed, 1)));
        // 同一行、同样距离与时长的跑马灯已在滚动 → 不重启。
        // PositionCard / PlaybackInfoChanged 等高频调用会反复走到这里，每次都从 0 重启会让文字永远停在开头。
        // 距离按 8px 粒度量化：任务栏轮询带来的 1px 级宽度抖动不会触发重启，只有真正换行/大变宽才重启。
        var quantizedDistance = Math.Round(distance / 8) * 8;
        var signature = $"{LyricTextBright.Text}|{LyricTranslation.Text}|{LyricTranslation.Visibility}|{quantizedDistance:F0}|{duration.TotalSeconds:F0}";
        if (signature == _marqueeSignature)
            return;

        _marqueeSignature = signature;
        AppLog.Debug($"跑马灯启动: 距离={distance:F0} 时长={duration.TotalSeconds:F0}s 文本='{LyricTextBright.Text}'");
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
