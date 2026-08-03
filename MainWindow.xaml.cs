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
    private const double MarqueeSpeedDipsPerSecond = 55; // 跑马灯速度（DIP/秒）
    private const double MarqueeEndPadding = 24; // 滚到最右侧时预留的空白

    // 第一版默认开启点击穿透；想用鼠标点卡片时临时改成 false
    private const bool EnableClickThrough = true;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);


    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- 状态 ----
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly LyricsService _lyrics = new();
    private readonly TaskbarService _taskbar = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private string _currentKey = string.Empty;
    private int _currentIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => PositionCard();
        _ticker.Tick += OnTickerTick;
        LyricHost.SizeChanged += (_, _) => ReapplyLyricDisplay();
        _taskbar.TaskbarChanged += PositionCard;

        // 兜底：2 秒后无论如何显示，避免定位失败时窗口一直隐藏
        var safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        safetyTimer.Tick += (_, _) =>
        {
            safetyTimer.Stop();
            if (Visibility != Visibility.Visible)
                Visibility = Visibility.Visible;
        };
        safetyTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
        PositionCard();

        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        RegisterHotKey(hwnd, HotKeyId, ModControl | ModAlt, VkQ);
    }

    /// <summary>置顶、不抢焦点；按设置开启鼠标点击穿透</summary>
    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_NOACTIVATE;
        if (EnableClickThrough)
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

        var taskbarRect = _taskbar.GetTaskbarRectDips(this);
        var widget = _taskbar.GetFluentFlyoutWidgetRectDips(this);
        var start = _taskbar.GetStartButtonRectDips(this);

        // 左边缘：FluentFlyout 组件右边缘（不覆盖组件，避免叠层闪烁）
        double left = widget is { } w
            ? w.Right + ComponentGap
            : taskbarRect.Left;

        // 右边缘：开始按钮左侧；找不到开始按钮时用任务栏中线兜底（开始按钮居中）
        double rightEdge = start is { } s
            ? s.Left - CardRightGap
            : taskbarRect.Left + taskbarRect.Width / 2;

        var cardWidth = Math.Max(120, Math.Min(rightEdge - left, taskbarRect.Width - left));

        Debug.WriteLine($"[FluentLyrics] 卡片 {left:F0}..{rightEdge:F0} 宽={cardWidth:F0}");

        Left = left;
        Top = taskbarRect.Top;
        Width = cardWidth;
        Height = Math.Max(taskbarRect.Height, 36);

        ShowCardIfReady();
        ReapplyLyricDisplay();
    }

    /// <summary>定位成功后再显示，避免启动瞬间以默认位置盖住 FluentFlyout 组件</summary>
    private void ShowCardIfReady()
    {
        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            manager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(() => UpdateSession(manager.GetCurrentSession()));
            UpdateSession(manager.GetCurrentSession());
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
        }

        _session = session;

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

            _currentKey = key;
            _currentIndex = -1;
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

            var loaded = await _lyrics.LoadAsync(title, artist, durationMs);

            // 等待期间可能已经切到下一首歌，旧结果直接丢弃
            if (key != _currentKey)
                return;

            _currentIndex = -1;
            SetStatus(loaded ? string.Empty : "未找到歌词");
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

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        // 歌词推进由定时器驱动，这里不需要做任何事
    }

    /// <summary>定时器：按播放位置取当前歌词行，只在行变化时刷新界面</summary>
    private void OnTickerTick(object? sender, EventArgs e)
    {
        var session = _session;
        if (session is null || _lyrics.Lines.Count == 0)
            return;

        int index;
        try
        {
            index = _lyrics.GetLineIndexAt(GetPositionMs(session));
        }
        catch
        {
            return;
        }

        if (index == _currentIndex)
            return;

        _currentIndex = index;
        SetStatus(index >= 0 ? _lyrics.Lines[index].Text : string.Empty);
    }

    /// <summary>
    /// 计算当前播放位置（毫秒）。
    /// SMTC 的 Position 是"上次更新时刻"的快照，QQ 音乐约每秒才推送一次，
    /// 直接使用会导致歌词切换滞后；这里用 LastUpdatedTime 外推到当前时刻。
    /// </summary>
    private static int GetPositionMs(GlobalSystemMediaTransportControlsSession session)
    {
        var timeline = session.GetTimelineProperties();
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

        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        return (int)position.TotalMilliseconds;
    }

    private void SetStatus(string text)
    {
        Dispatcher.Invoke(() => UpdateLyricDisplay(text));
    }

    /// <summary>显示歌词/状态文本：短文本居中，超长文本跑马灯滚动</summary>
    private void UpdateLyricDisplay(string text)
    {
        LyricText.Text = text ?? string.Empty;
        ReapplyLyricDisplay();
    }

    private void ReapplyLyricDisplay()
    {
        if (LyricHost.ActualWidth <= 0)
            return;

        LyricText.UpdateLayout();
        var hostWidth = LyricHost.ActualWidth;
        var hostHeight = LyricHost.ActualHeight;
        var textWidth = LyricText.ActualWidth;
        var availableWidth = Math.Max(0, hostWidth - _textBaseLeft);

        // 垂直居中
        Canvas.SetTop(LyricText, Math.Max(0, (hostHeight - LyricText.ActualHeight) / 2));

        // 短文本：在"组件右侧到卡片末尾"区域内静态居中
        if (textWidth <= availableWidth - 8)
        {
            LyricText.BeginAnimation(TranslateTransform.XProperty, null);
            Canvas.SetLeft(LyricText, _textBaseLeft + Math.Max(0, (availableWidth - textWidth) / 2));
            LyricTranslate.X = 0;
            return;
        }

        // 超长文本：跑马灯（从组件右侧开始，向左滚出再返回）
        Canvas.SetLeft(LyricText, _textBaseLeft);
        var distance = textWidth - availableWidth + MarqueeEndPadding;
        if (distance <= 0)
        {
            LyricText.BeginAnimation(TranslateTransform.XProperty, null);
            LyricTranslate.X = 0;
            return;
        }

        var duration = TimeSpan.FromSeconds(Math.Max(4, distance / MarqueeSpeedDipsPerSecond));
        var animation = new DoubleAnimation(0, -distance, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        LyricTranslate.X = 0;
        LyricText.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    // ---- 全局热键：Ctrl+Alt+Q 退出 ----
    private const int HotKeyId = 0x5A5A;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint VkQ = 0x51;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotKeyId) // WM_HOTKEY
        {
            Close();
            handled = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HotKeyId);
        _taskbar.Dispose();
        base.OnClosed(e);
    }
}