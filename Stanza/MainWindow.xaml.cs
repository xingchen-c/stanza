using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stanza.Models;
using Stanza.Services;
using Stanza.Services.Desktop;
using Stanza.Services.Lyrics;
using Stanza.Services.Playback;
using Stanza.Services.Taskbar;

namespace Stanza;

/// <summary>
///     任务栏歌词主窗口：负责窗口定位、命中和交互，消费 SmtcSessionService 的状态与事件，
///     不直接访问原始 WinRT SMTC 对象。
/// </summary>
public partial class MainWindow
{
    private const int WmNchittest = 0x0084; // 命中测试：按区域决定可点击/穿透
    private const int Htclient = 1; // 窗口客户区（可点击）
    private const double MarqueeEndPadding = 24; // 滚到最右侧时预留的空白
    private readonly TaskbarService _taskbar = new();
    private readonly ForegroundFullscreenDetector _fullscreenDetector = new();
    private readonly WindowInteropService _windowInterop = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TrayIconService _tray = new();
    private readonly Dictionary<string, double[]> _wordBoundaryCache = new(); // 行文本 → 各字结束位置的像素宽度
    private static readonly Brush SoftwareRenderCardBrush = CreateSoftwareRenderCardBrush(); // 降级模式半透明深色卡片底
    private bool _adjustingPosition; // 托盘"调整歌词位置"模式（临时关闭点击穿透）
    private bool _clickThrough; // 鼠标点击穿透
    private int _currentIndex = -1;

    private System.Windows.Point _dragGrabOffset; // 光标(屏幕DIP)相对窗口左上角的抓取偏移
    private bool _dragging; // 调整模式下正在拖动卡片
    private bool _hiddenForFullscreen; // 前台窗口全屏（游戏/视频）时隐藏卡片
    private bool _hiddenForPlayback;
    private bool _userHidden; // 托盘手动隐藏：自动定位不得重新显示
    private bool _hoverShowing; // 鼠标悬停在卡片上：显示歌名/歌手 + 控制按钮
    private HwndSource? _hwndSource; // 窗口消息钩子（WM_NCHITTEST 区域点击穿透）
    private double _lastClipWidth = -1; // 上次裁剪宽度（逐字/线性共用去重）
    private double _animatedClipWidth = -1; // 卡拉OK亮色边界当前显示宽度（平滑趋近目标，渐入渐出）
    private double _lastProgress = -1; // 上次渲染的进度（0~1）
    private Brush _lyricBrush = Brushes.White; // 状态文本/亮色层画刷
    private Color _lyricColor = Colors.White;
    private Brush _lyricDimBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)); // 暗色层画刷
    private Color _lyricDimColor = Color.FromArgb(110, 255, 255, 255);
    private string _reapplyDiagSig = string.Empty; // 布局诊断签名（仅变化时打印，防刷屏）
    private bool? _lastRenderDowngraded; // 渲染降级上次状态（日志按变化打印 Info，其余 Debug）

    /// <summary>渲染跳过原因的上次值（同一原因连续跳过不重复打日志）</summary>
    private string? _lastSkipReason;

    /// <summary>位置采样诊断的节流（2 秒）</summary>
    private int _lastPositionSampleLogMs;

    // ---- 运行时设置（来自 AppSettings）----
    private AppSettings _settings = AppSettingsService.Load();
    private bool _showMediaControls; // 显示封面与播放控制（媒体区）

    // 歌词文本在卡片内的起始 x（媒体模式下 = 封面宽 + 间距，纯歌词模式从 0 开始）
    private double _textBaseLeft;

    private SmtcSessionService _smtc = null!;
    private bool _sessionConnected;
    private LyricsPickerWindow? _lyricsPicker;

    /// <summary>WPF StartupUri 使用的无参入口；组合根由 App.OnStartup 提前创建。</summary>
    public MainWindow()
        : this(((App)Application.Current).Services)
    {
    }

    /// <summary>初始化窗口、服务、事件订阅、ticker 和任务栏调度。</summary>
    internal MainWindow(AppServices services)
    {
        InitializeComponent();
        _smtc = new SmtcSessionService(
            Dispatcher,
            services.LyricsCache,
            services.LyricsChoices);
        _clickThrough = _settings.ClickThrough;
        _showMediaControls = _settings.ShowMediaControls;
        ApplyLyricBrushes();
        AppLog.ApplyLevel(_settings.LogLevel);
        _smtc.SetMediaControlsEnabled(_showMediaControls);

        // 订阅 SMTC 服务事件：状态/歌词/封面变化 → 刷新 UI
        _smtc.SessionChanged += OnSessionChanged;
        _smtc.StatusChanged += OnServiceStatus;
        _smtc.TrackSwitchStarted += OnTrackSwitchStarted;
        _smtc.TrackLyricsChanged += OnTrackLyricsChanged;
        _smtc.PlaybackStatusChanged += OnServicePlaybackStatus;
        _smtc.PlaybackCapabilitiesChanged += OnPlaybackCapabilitiesChanged;
        _smtc.CoverChanged += OnCoverChanged;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) =>
        {
            _wordBoundaryCache.Clear(); // 逐字宽度依赖 DPI，跨缩放显示器移动后必须重算
            PositionCard();
        };
        _ticker.Tick += OnTickerTick;
        // 无播放会话也要采样悬停：整窗穿透时收不到 WPF 鼠标事件。
        _ticker.Start();
        OnPlaybackCapabilitiesChanged(_smtc.Capabilities);
        UpdateLyricsPickerAvailability();
        // 全屏检测独立于播放会话：QQ 音乐未运行（无 SMTC 会话）时也要能隐藏卡片
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenState();
        _fullscreenTimer.Start();
        LyricHost.SizeChanged += (_, _) => ReapplyLyricDisplay();
        _taskbar.TaskbarChanged += PositionCard;
        _taskbar.ApplySettings(_settings);
        // 渲染能力变化（远程桌面接入/断开、驱动变化）时重新应用降级
        RenderCapability.TierChanged += OnRenderTierChanged;

        // 托盘：显示/隐藏、设置、打开日志目录、退出（Ctrl+Alt+Q 与 QQ 音乐快捷键冲突，已移除）
        _tray.ShowHideRequested += ToggleCardVisibility;
        _tray.SettingsRequested += OpenSettings;
        _tray.OpenLogDirectoryRequested += OpenLogDirectory;
        _tray.ClearCacheRequested += () =>
        {
            _smtc.ClearLyricsData();
        };
        _tray.LyricsSelectionRequested += ShowLyricsPicker;
        _tray.AdjustPositionRequested += ToggleAdjustPosition;
        _tray.ResetPositionRequested += ResetManualOffset;
        _tray.ExitRequested += Close;

        // 拖动调整位置：仅"调整模式"下窗口能收到鼠标（平时点击穿透）
        MouseLeftButtonDown += OnAdjustMouseDown;
        MouseMove += OnAdjustMouseMove;
        MouseLeftButtonUp += OnAdjustMouseUp;
        MouseRightButtonDown += OnAdjustMouseRightDown;

        // 兜底：2 秒后重试定位，任务栏仍未就绪时按显隐条件显示。
        var safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        safetyTimer.Tick += (_, _) =>
        {
            safetyTimer.Stop();
            PositionCard(); // 重新定位一次：定位成功自己显示，组件隐藏则保持隐藏
            if (Visibility != Visibility.Visible && !_taskbar.HasTaskbar && !_userHidden)
                ShowCardIfReady(); // 任务栏还没就绪才兜底显示，仍遵守全屏和播放状态
        };

        safetyTimer.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _smtc.InitializeAsync();
    }

    /// <summary>SMTC 会话接入/断开：刷新显隐、封面与渲染状态；交互采样保持运行。</summary>
    private void OnSessionChanged(bool connected)
    {
        _currentIndex = -1;
        _lastProgress = -1;
        _animatedClipWidth = -1;

        CoverImage.Source = null;
        // 封面位常驻：会话接入时显示占位（无封面显示音符），断开时收起
        CoverBorder.Visibility = connected && _showMediaControls && _smtc.Capabilities.ProvidesCover
            ? Visibility.Visible
            : Visibility.Collapsed;
        LyricViewport.Visibility = Visibility.Visible;
        HoverPanel.Visibility = Visibility.Collapsed;
        _hoverShowing = false;
        _sessionConnected = connected;
        UpdateLyricsPickerAvailability();
        PositionCard();
    }

    /// <summary>服务状态文本（"正在获取歌词…"/"未找到歌词"/空串恢复）：更新显示并重置渲染</summary>
    private void OnServiceStatus(string text)
    {
        _currentIndex = -1;
        _lastProgress = -1;
        _animatedClipWidth = -1;
        UpdateLyricDisplay(text);
    }

    /// <summary>切歌开始：清掉当前渲染状态，等待新歌词</summary>
    private void OnTrackSwitchStarted()
    {
        _currentIndex = -1;
        _lastProgress = -1;
        _animatedClipWidth = -1;
        _wordBoundaryCache.Clear(); // 上一首歌的逐字宽度缓存作废（常驻工具防无界增长）
    }

    /// <summary>歌词加载完成：重置渲染状态，ticker 下一拍按位置渲染</summary>
    private void OnTrackLyricsChanged(string key, LyricsLoadResult result)
    {
        _currentIndex = -1;
        _lastProgress = -1;
        _animatedClipWidth = -1;
    }

    /// <summary>播放状态变化：刷新播放/暂停图标与卡片显隐</summary>
    private void OnServicePlaybackStatus(PlaybackState status)
    {
        PlayPauseButton.Content = status == PlaybackState.Playing
            ? "\uE769"
            : "\uE768";
        PositionCard();
    }

    /// <summary>播放器运行时能力变化：驱动控制按钮和封面区显隐。</summary>
    private void OnPlaybackCapabilitiesChanged(PlaybackCapabilities capabilities)
    {
        PrevButton.IsEnabled = capabilities.SupportsPrevious;
        PlayPauseButton.IsEnabled = capabilities.SupportsPlayPause;
        NextButton.IsEnabled = capabilities.SupportsNext;
        PrevButton.ToolTip = capabilities.SupportsPrevious ? "上一首" : "播放器未开放上一首控制";
        PlayPauseButton.ToolTip = capabilities.SupportsPlayPause ? "播放 / 暂停" : "播放器未开放播放 / 暂停控制";
        NextButton.ToolTip = capabilities.SupportsNext ? "下一首" : "播放器未开放下一首控制";

        if (!capabilities.ProvidesCover)
        {
            CoverImage.Source = null;
            CoverBorder.Visibility = Visibility.Collapsed;
            return;
        }

        CoverBorder.Visibility = _showMediaControls
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>封面就绪/变化：更新封面显示并重算歌词布局（null = 无封面/失败）</summary>
    private void OnCoverChanged(string key, BitmapSource? source)
    {
        // 封面位常驻（显隐由 _showMediaControls / 会话状态控制）：无封面显示占位音符，
        // 有封面显示图，不再整体隐藏，避免切歌时"封面消失→出现"的跳跃；歌词宽度也随之稳定。
        CoverImage.Source = source;
        // 渐入：封面图从透明淡入，避免"占位→图"硬切（渐入渐出）
        if (source is not null)
        {
            CoverImage.Opacity = 0;
            CoverImage.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            CoverImage.BeginAnimation(UIElement.OpacityProperty, null);
            CoverImage.Opacity = 1;
        }

        ReapplyLyricDisplay();
    }

    /// <summary>悬停状态切换：隐藏歌词（保留滚动位置），显示"歌名/歌手 + 控制按钮"面板</summary>
    private void ApplyHoverState()
    {
        if (_hoverShowing && _showMediaControls && !_hiddenForFullscreen)
        {
            TrackTitleText.Text = _smtc.CurrentTitle;
            TrackArtistText.Text = _smtc.CurrentArtist;

            // 面板紧贴封面右侧（封面隐藏时贴窗口左缘），不甩到窗口最右
            var coverRight = CoverBorder.Visibility == Visibility.Visible
                ? CoverBorder.Margin.Left + CoverBorder.ActualWidth
                : 0;
            // 曲目信息宽度：窗口宽 - 封面偏移 - 按钮组(4×26+2) - 间距，窄卡片自动压缩
            var maxInfoWidth = Math.Max(60, Width - coverRight - 140);
            TrackTitleText.MaxWidth = maxInfoWidth;
            TrackArtistText.MaxWidth = maxInfoWidth;
            // 信息区太窄时只留歌名，歌手行收起
            TrackArtistText.Visibility = maxInfoWidth < 90
                ? Visibility.Collapsed
                : Visibility.Visible;
            HoverPanel.HorizontalAlignment = HorizontalAlignment.Left;
            HoverPanel.Margin = new Thickness(coverRight + 8, 0, 0, 0);
            // 隐藏歌词（保留布局与滚动位置），显示歌手信息 + 控制按钮
            LyricViewport.Visibility = Visibility.Hidden;
            AnimateVisibility(HoverPanel, true);
            // 强制同步布局：否则从隐藏切到显示的瞬间 ActualWidth 仍为 0，
            // WM_NCHITTEST 会跳过面板判定、按钮区域返回穿透（悬停/点击偶发无反馈）
            HoverPanel.UpdateLayout();
        }
        else
        {
            LyricViewport.Visibility = Visibility.Visible;
            AnimateVisibility(HoverPanel, false);
        }
    }

    /// <summary>媒体区：上一首</summary>
    private async void OnPrevClick(object sender, RoutedEventArgs e)
    {
        await _smtc.TrySkipPreviousAsync();
    }

    /// <summary>媒体区：播放/暂停</summary>
    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        await _smtc.TryTogglePlayPauseAsync();
    }

    /// <summary>媒体区：下一首</summary>
    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        await _smtc.TrySkipNextAsync();
    }

    /// <summary>悬停面板"换版本"：复用托盘"歌词选择…"同一路径</summary>
    private void OnSwitchVersionClick(object sender, RoutedEventArgs e)
    {
        ShowLyricsPicker();
    }

    /// <summary>托盘和按钮共享单个窗口；搜索开始前先显示加载态。</summary>
    private void ShowLyricsPicker()
    {
        if (_lyricsPicker is { } existing)
        {
            existing.Activate();
            return;
        }

        var context = _smtc.CreateLyricsSelectionContext();
        if (context is null)
        {
            MessageBox.Show("暂无可选择歌词的曲目，请先播放歌曲。", "选择歌词");
            return;
        }

        var window = new LyricsPickerWindow(
            context,
            _smtc.GetLyricCandidatesAsync,
            _smtc.ApplyLyricCandidateAsync,
            _smtc.IsLyricsSelectionCurrent)
        {
            Owner = this
        };
        _lyricsPicker = window;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _lyricsPicker = null;
        }
    }

    private void UpdateLyricsPickerAvailability()
    {
        var available = _smtc.CreateLyricsSelectionContext() is not null;
        SwitchVersionButton.IsEnabled = available;
        SwitchVersionButton.ToolTip = available ? "换歌词版本" : "暂无可选择歌词的曲目";
        _lyricsPicker?.RefreshTrackContext();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        ApplyWindowStyles();
        PositionCard();
        ApplyRenderMode(); // 窗口句柄就绪后再判定渲染能力（TierChanged 也可能在此之前触发过）
    }

    /// <summary>是否软件渲染（无 GPU 加速，WPF 全部走 CPU 合成；Tier 高 16 位为 0）</summary>
    private static bool IsSoftwareRendered() => (RenderCapability.Tier >> 16) == 0;

    /// <summary>
    ///     渲染降级（软件渲染环境）：文字改 Grayscale 抗锯齿、卡片垫半透明深色底、
    ///     降低 ticker 刷新率——让无显卡驱动的机器"能看清、不闪"。
    ///     有 GPU 的机器（Tier ≥ 1）完全不受影响；兼容模式设置变化时即时重应用。
    ///     （阴影已改用偏移伪阴影，不依赖 GPU blur，不再随降级开关。）
    /// </summary>
    private void ApplyRenderMode()
    {
        var downgrade = _settings.CompatibilityMode switch
        {
            "ForceOn" => true,
            "ForceOff" => false,
            _ => IsSoftwareRendered()
        };

        if (downgrade)
        {
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            LyricHost.Background = SoftwareRenderCardBrush;
            _ticker.Interval = TimeSpan.FromMilliseconds(250);
        }
        else
        {
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            LyricHost.Background = Brushes.Transparent;
            _ticker.Interval = TimeSpan.FromMilliseconds(100);
        }

        var tier = RenderCapability.Tier >> 16;
        if (_lastRenderDowngraded != downgrade)
        {
            _lastRenderDowngraded = downgrade;
            AppLog.Info($"渲染模式: Tier={tier} 降级={downgrade}（CompatibilityMode={_settings.CompatibilityMode}）");
        }
        else
        {
            AppLog.Debug($"渲染模式: Tier={tier} 降级={downgrade}（CompatibilityMode={_settings.CompatibilityMode}）");
        }
    }

    /// <summary>渲染能力变化（远程桌面接入/断开、驱动变化）：重新应用降级</summary>
    private void OnRenderTierChanged(object? sender, EventArgs e) => ApplyRenderMode();

    /// <summary>降级模式卡片底：≈80% 不透明深色，遮软件渲染下的污渍并减少 per-pixel alpha 合成负担</summary>
    private static Brush CreateSoftwareRenderCardBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xCC, 0x1F, 0x1F, 0x1F));
        brush.Freeze();
        return brush;
    }

    /// <summary>按光标所在的真实交互区域切换分层窗口穿透，不依赖同线程命中转发。</summary>
    private void ApplyWindowStyles()
    {
        var cursor = _windowInterop.GetCursorPositionPixels();
        UpdatePointerInteraction((int)cursor.X, (int)cursor.Y);
    }

    private void UpdatePointerInteraction(int screenX, int screenY)
    {
        var hovering = false;
        if (IsVisible && _hwndSource is not null && _showMediaControls && !_hiddenForFullscreen && !_adjustingPosition)
        {
            var cursor = PointFromScreen(new System.Windows.Point(screenX, screenY));
            hovering = cursor.X >= 0 && cursor.X <= ActualWidth && cursor.Y >= 0 && cursor.Y <= ActualHeight;
        }

        if (hovering != _hoverShowing)
        {
            _hoverShowing = hovering;
            ApplyHoverState();
        }
        else if (hovering && (TrackTitleText.Text != _smtc.CurrentTitle || TrackArtistText.Text != _smtc.CurrentArtist))
        {
            // 悬停切歌只刷新文字和命中布局，不重启淡入动画。
            TrackTitleText.Text = _smtc.CurrentTitle;
            TrackArtistText.Text = _smtc.CurrentArtist;
            HoverPanel.UpdateLayout();
        }

        var mediaHit = IsVisible && _hwndSource is not null && _showMediaControls &&
                       !_hiddenForFullscreen && IsInMediaRegion(screenX, screenY);
        _windowInterop.ApplyStyles(this, _adjustingPosition, _clickThrough, mediaHit);
    }

    /// <summary>
    ///     WM_NCHITTEST 区域命中：媒体控制模式下封面/按钮可点击、歌词区穿透；
    ///     拖动调整模式下整窗可交互；关闭点击穿透时整窗可点击。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNchittest)
            return IntPtr.Zero;

        // lParam：屏幕坐标（物理像素），低 16 位 x、高 16 位 y（可能为负）
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        if (_adjustingPosition)
        {
            handled = true;
            return new IntPtr(Htclient); // 调整模式交给 WPF 拖动逻辑
        }

        // 有命中消息时同步采样，整窗已穿透时则由独立于会话的 ticker 唤回交互。
        UpdatePointerInteraction(x, y);
        if (!_clickThrough || (_showMediaControls && IsInMediaRegion(x, y)))
        {
            handled = true;
            return new IntPtr(Htclient);
        }

        return IntPtr.Zero; // 跨进程穿透由 WS_EX_TRANSPARENT 实现，不返回 HTTRANSPARENT。
    }

    /// <summary>
    ///     鼠标点（屏幕物理像素）是否落在封面或控制按钮区域。
    ///     PointToScreen 返回物理像素坐标，与 WM_NCHITTEST 的 lParam 直接可比，避免 DIP 换算偏差。
    /// </summary>
    private bool IsInMediaRegion(int screenX, int screenY)
    {
        if (CoverBorder.Visibility == Visibility.Visible && CoverBorder.ActualWidth > 0)
        {
            var tl = CoverBorder.PointToScreen(new System.Windows.Point(0, 0));
            var br = CoverBorder.PointToScreen(
                new System.Windows.Point(CoverBorder.ActualWidth, CoverBorder.ActualHeight));
            if (screenX >= tl.X && screenX <= br.X && screenY >= tl.Y && screenY <= br.Y)
                return true;
        }

        if (HoverPanel.Visibility == Visibility.Visible && HoverPanel.ActualWidth > 0)
        {
            var tl = HoverPanel.PointToScreen(new System.Windows.Point(0, 0));
            var br = HoverPanel.PointToScreen(
                new System.Windows.Point(HoverPanel.ActualWidth, HoverPanel.ActualHeight));
            return screenX >= tl.X && screenX <= br.X && screenY >= tl.Y && screenY <= br.Y;
        }

        return false;
    }

    /// <summary>独立的全屏检测定时器回调：不依赖播放会话，任何前台状态下都检查</summary>
    private void UpdateFullscreenState()
    {
        var fullscreen = _fullscreenDetector.IsForegroundFullscreen(
            _taskbar.GetTaskbarRectPixels());
        // WPF 启动时可能调用 Show，全屏期间仍需纠正窗口显隐。
        if (fullscreen == _hiddenForFullscreen && (!fullscreen || Visibility == Visibility.Collapsed))
            return;

        if (fullscreen != _hiddenForFullscreen)
            AppLog.Info($"全屏隐藏: {(fullscreen ? "进入" : "退出")}");
        _hiddenForFullscreen = fullscreen;
        PositionCard();
    }

    private void PositionCard()
    {
        if (_userHidden || _hiddenForFullscreen || ShouldHideForPlayback())
        {
            if (Visibility != Visibility.Collapsed)
                Visibility = Visibility.Collapsed;
            return;
        }

        if (!_taskbar.HasTaskbar)
            return;

        // 调整模式下位置由用户拖动控制，不自动定位（退出时再恢复锚点+偏移）
        if (_adjustingPosition)
        {
            ShowCardIfReady();
            return;
        }

        var taskbarRect = _taskbar.GetTaskbarRectDips(this);
        if (_taskbar.GetCardRegionDips(this) is not { } card)
            return; // 还没算出来，等下一轮定位

        var cardWidth = _settings.AutoCardWidth
            ? Math.Max(120, card.Width)
            : Math.Max(120, _settings.CardWidth);
        // 媒体控制开启时（无论自动/手动宽度），保证封面(42)+按钮(~100)+最小歌词区放得下，
        // 否则按钮被挤出、歌词在按钮下面滚（看起来像跑马灯失效）
        if (_showMediaControls)
            cardWidth = Math.Max(cardWidth, 240);
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
        if (_smtc.PlaybackState == PlaybackState.Unknown)
            return false;

        // 切歌时 QQ 会短暂发 Stopped（切换序列的一部分），不是真的停止播放：
        // 此时保持卡片可见，避免整卡「藏一下→再出现」的闪烁。
        if (_smtc.IsSwitchingTrack)
            return false;

        // 停止/关闭始终隐藏；暂停是否隐藏由设置决定（可保持显示方便看歌词）
        return _smtc.PlaybackState switch
        {
            PlaybackState.Stopped or PlaybackState.Closed => true,
            PlaybackState.Paused => _settings.HideOnPause,
            _ => false
        };
    }

    /// <summary>托盘菜单：切换卡片显隐，显示时仍遵守全屏与播放状态。</summary>
    private void ToggleCardVisibility()
    {
        if (Visibility == Visibility.Visible)
        {
            _userHidden = true;
            Visibility = Visibility.Collapsed;
        }
        else
        {
            _userHidden = false;
            PositionCard();
            ShowCardIfReady();
        }
    }

    /// <summary>托盘菜单：打开设置窗口；保存后重新应用设置</summary>
    private void OpenSettings()
    {
        // 不设 Owner：主窗口是置顶的，附属窗口会跟着进置顶层；设置窗口应是普通窗口。
        // ShowDialog 保持模态，居中改为屏幕中心（主窗口在任务栏，相对它居中会偏出屏幕）。
        var savedSettings = _settings; // 进入设置前状态，取消时回滚
        var saved = false;
        var window = new SettingsWindow(_settings);
        window.PreviewChanged += preview =>
        {
            _settings = preview;
            ApplySettings(); // 实时预览，不落盘
        };
        window.Saved += () =>
        {
            saved = true;
            _settings = AppSettingsService.Load();
            AppLog.ApplyLevel(_settings.LogLevel); // 日志级别改了即时生效（以后设置窗口加这个选项就不用重启）
            ApplySettings();
        };
        window.ShowDialog();

        // 取消 / 直接关闭：回滚到进入设置前的状态
        if (!saved)
        {
            _settings = savedSettings;
            ApplySettings();
        }
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
            ExitAdjustMode(true); // 再点一次 = 保存并退出
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            _userHidden = false;
            ShowCardIfReady();
        }

        _adjustingPosition = true;
        _clickThrough = false;
        _currentIndex = -1;
        _lastProgress = -1;
        _animatedClipWidth = -1;
        ApplyWindowStyles();
        _tray.SetAdjusting(true);
        SetStatus("拖动卡片调整位置，右键完成");
        AppLog.Info("进入位置调整模式（点击穿透已临时关闭）");
    }

    /// <summary>托盘菜单：清除手动偏移，回到自动定位</summary>
    private void ResetManualOffset()
    {
        if (_adjustingPosition)
            ExitAdjustMode(false);

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
        _animatedClipWidth = -1;
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
        _dragGrabOffset = new System.Windows.Point(Left - cursor.X, Top - cursor.Y);
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
    private System.Windows.Point GetCursorPosDips()
    {
        return _windowInterop.GetCursorPositionDips(this);
    }

    private void OnAdjustMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!_adjustingPosition)
            return;

        e.Handled = true;
        ExitAdjustMode(true); // 右键 = 保存并退出
    }

    /// <summary>保存设置后重应用：字号/颜色/点击穿透/媒体区等</summary>
    private void ApplySettings()
    {
        _taskbar.ApplySettings(_settings);
        _clickThrough = _settings.ClickThrough;
        _showMediaControls = _settings.ShowMediaControls;
        ApplyLyricBrushes();
        _wordBoundaryCache.Clear(); // 字号可能变化，逐字宽度缓存作废
        _lastClipWidth = -1;
        _animatedClipWidth = -1;
        LyricTextShadow.FontSize = _settings.FontSize;
        LyricTextBright.FontSize = _settings.FontSize;
        LyricTextDim.FontSize = _settings.FontSize;
        LyricTranslationShadow.FontSize = Math.Max(10, _settings.FontSize - 3);
        LyricTranslation.FontSize = Math.Max(10, _settings.FontSize - 3);
        LyricTextBright.Foreground = _lyricBrush;
        LyricTextDim.Foreground = _lyricDimBrush;

        // 媒体区开关：通知服务（封面加载开关），UI 侧隐藏/恢复封面与按钮
        _smtc.SetMediaControlsEnabled(_showMediaControls);
        if (!_showMediaControls)
        {
            CoverImage.Source = null;
            CoverBorder.Visibility = Visibility.Collapsed;
            LyricViewport.Visibility = Visibility.Visible;
            HoverPanel.Visibility = Visibility.Collapsed;
            _hoverShowing = false;
        }
        else
        {
            CoverBorder.Visibility = _smtc.Capabilities.ProvidesCover
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        ApplyWindowStyles(); // 更新点击穿透需要重设窗口扩展样式
        _currentIndex = -1;
        _lastProgress = -1;
        PositionCard(); // 布局变化（封面/按钮占位）重新计算卡片与歌词可用宽度
        ReapplyLyricDisplay(); // 下一拍（100ms 内）会用新颜色重绘当前行
        _ = _smtc.RefreshTrackAsync(); // 开启媒体区后立即补一次封面
        ApplyRenderMode(); // 兼容模式设置变化即时生效
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

    /// <summary>显示入口统一遵守手动隐藏、全屏与播放条件。</summary>
    private void ShowCardIfReady()
    {
        if (_userHidden || _hiddenForFullscreen || ShouldHideForPlayback())
            return;
        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
    }

    /// <summary>
    ///     定时器：每拍检查播放状态与悬停状态驱动显隐；渲染当前歌词进度。
    ///     切歌状态机（位置回退检测/误判恢复/看门狗）由 SmtcSessionService 统一管理。
    /// </summary>
    private void OnTickerTick(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
        UpdateLyricsPickerAvailability();
        // 无会话仍采样交互；调整模式不让卡拉 OK 裁剪盖住提示文字。
        if (!_sessionConnected || _adjustingPosition)
            return;

        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sample = _smtc.SamplePlayback(nowUtcMs);

        // 播放状态变化 → 立即刷新卡片显隐（PlaybackInfoChanged 事件之外的 100ms 兜底）
        var hide = ShouldHideForPlayback();
        if (hide != _hiddenForPlayback)
        {
            _hiddenForPlayback = hide;
            PositionCard();
        }

        // 位置链路常驻诊断（Debug 级，2s 节流）：观察各播放器快照行为差异的仪器
        if (Environment.TickCount - _lastPositionSampleLogMs > 2000)
        {
            _lastPositionSampleLogMs = Environment.TickCount;
            AppLog.Debug(
                $"位置采样: 原始={sample.RawPositionMs}ms " +
                $"计算={sample.PositionMs}ms " +
                $"状态={sample.PlaybackState} " +
                $"决策={(sample.ShouldRender ? "渲染" : sample.SkipReason)}");
        }

        // 渲染闸门（旧快照窗口 / 位置毛刺）已由 Tracker 收口，跳过原因变化时打一条
        if (!sample.ShouldRender)
        {
            if (sample.SkipReason != _lastSkipReason)
            {
                _lastSkipReason = sample.SkipReason;
                AppLog.Debug($"渲染跳过: {sample.SkipReason}");
            }

            return;
        }

        _lastSkipReason = null;

        // 全局歌词偏移（用户偏好，渲染层叠加，不属于位置真相）：正数 = 歌词提前显示
        var positionMs = Math.Max(0, sample.PositionMs + _settings.LyricOffsetMs);

        // 等待新歌歌词时不渲染，占位由 StatusChanged 事件驱动
        if (_smtc.IsSwitchingTrack)
            return;

        var lines = _smtc.Lyrics.Lines;
        if (lines.Count == 0)
            return;

        int index;
        try
        {
            index = _smtc.Lyrics.GetLineIndexAt(positionMs);
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

        ShowLyricLine(lines[index], positionMs);
    }

    /// <summary>窗口关闭时停止定时器、取消服务事件并释放托盘和任务栏资源。</summary>
    protected override void OnClosed(EventArgs e)
    {
        RenderCapability.TierChanged -= OnRenderTierChanged; // 退订静态事件，避免常驻引用
        _lyricsPicker?.Close();
        _smtc?.Dispose(); // 退订 SMTC 事件、取消"未找到歌词"恢复调度
        _fullscreenTimer.Stop();
        _ticker.Stop();
        _tray.Dispose();
        _taskbar.Dispose();
        base.OnClosed(e);
    }

}
