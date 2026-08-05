using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FluentLyrics.Models;
using Microsoft.Win32;

namespace FluentLyrics.Services.Taskbar;

/// <summary>
/// 任务栏定位服务（支持多显示器）：
/// 枚举所有任务栏（Shell_TrayWnd / Shell_SecondaryTrayWnd），
/// 按白名单过滤定位器：探到锚点组件时按放置方向计算卡片区域，
/// 否则用独立定位 / 兜底区域；组件暂隐时保留上次选中的任务栏与位置。
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const string IndependentLocatorKey = "Independent";
    private const int CardGap = 8;
    private const int MaxCardWidth = 600;
    private const int MinCardWidth = 120;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly ITaskbarWidgetLocator[] _locators;
    private string _layoutSignature = string.Empty;

    // 白名单（定位器 Key）与放置方向，来自 AppSettings；优先级按列表顺序（FluentFlyout > Independent）
    private HashSet<string> _enabledKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "FluentFlyout",
        IndependentLocatorKey,
    };
    private string _placementDirection = "Auto";

    // 当前选中的任务栏（组件所在的那条；组件暂隐时保留上次）
    private IntPtr _taskbarHwnd;
    private RECT _taskbarRect;
    private bool _isPrimaryTaskbar = true;

    // 卡片区域缓存（屏幕物理像素）：单次探测失败保留上次结果，避免卡片跳位
    private PixelRect _cardRegion;
    private bool _hasCardRegion;
    private bool _wasAnchorBased; // 上次有效区域是否来自锚点组件（决定组件暂隐时是否粘性保留）

    /// <summary>是否拿到了有效的任务栏矩形</summary>
    public bool HasTaskbar => _taskbarRect.Width > 0 && _taskbarRect.Height > 0;

    /// <summary>当前选中的是否主任务栏（副屏任务栏没有开始按钮，定位回退策略不同）</summary>
    public bool IsPrimaryTaskbar => _isPrimaryTaskbar;

    /// <summary>本次探测组件是否真的在显示（不读粘性缓存，暂停播放组件隐藏时为 false）</summary>
    public bool IsWidgetPresent { get; private set; }

    /// <summary>任务栏矩形、组件可见区域或开始按钮位置变化时触发（已在 UI 线程）</summary>
    public event Action? TaskbarChanged;

    /// <summary>默认注册锚点定位器与独立定位器（白名单默认两者都启用）</summary>
    public TaskbarService()
        : this(new FluentFlyoutWidgetLocator(), new IndependentTaskbarLocator())
    {
    }

    /// <summary>可注入自定义组件定位器（按白名单过滤，顺序即优先级）</summary>
    public TaskbarService(params ITaskbarWidgetLocator[] locators)
    {
        _locators = locators.Length > 0
            ? locators
            : [new FluentFlyoutWidgetLocator(), new IndependentTaskbarLocator()];

        // 兜底轮询：任务栏自动隐藏、被移动、组件显隐、显示器变化等场景没有可靠事件，1 秒一次足够
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnSystemEvent;
        SystemEvents.UserPreferenceChanged += OnSystemEvent;

        Refresh();
    }

    /// <summary>应用定位相关设置（白名单 + 放置方向）；变化时立即重新定位</summary>
    public void ApplySettings(AppSettings settings)
    {
        var enabled = new HashSet<string>(
            settings.TaskbarLocatorWhitelist ?? [],
            StringComparer.OrdinalIgnoreCase);
        var direction = string.IsNullOrWhiteSpace(settings.PlacementDirection)
            ? "Auto"
            : settings.PlacementDirection;

        var changed = !enabled.SetEquals(_enabledKeys) ||
                      !string.Equals(direction, _placementDirection, StringComparison.OrdinalIgnoreCase);
        _enabledKeys = enabled;
        _placementDirection = direction;

        if (!changed)
            return;

        // 白名单/方向变化后清除锚点粘性，重新计算布局，避免旧组件位置残留
        _hasCardRegion = false;
        _wasAnchorBased = false;
        Refresh();
    }

    private void OnSystemEvent(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(new Action(Refresh));
    }

    /// <summary>立即重新读取任务栏、组件区域与开始按钮位置；有变化时触发 TaskbarChanged</summary>
    public void Refresh()
    {
        // 多显示器：枚举所有任务栏，优先选"探到锚点组件"的那条；找不到时用第一条（主任务栏）兜底
        IntPtr chosen = IntPtr.Zero;
        RECT chosenRect = default;
        bool chosenPrimary = true;
        PixelRect? widget = null;
        PixelRect? start = null;

        foreach (var hwnd in FindTaskbarHandles())
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
                continue;

            var tray = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var detected = DetectAnchorWidget(hwnd, tray);
            var startRect = DetectStartButtonRect(hwnd);

            if (chosen == IntPtr.Zero)
            {
                chosen = hwnd;
                chosenRect = rect;
                chosenPrimary = GetTaskbarClass(hwnd) == PrimaryTaskbarClass;
                widget = detected;
                start = startRect;
            }

            if (detected is not null)
            {
                chosen = hwnd;
                chosenRect = rect;
                chosenPrimary = GetTaskbarClass(hwnd) == PrimaryTaskbarClass;
                widget = detected;
                start = startRect;
                break;
            }
        }

        if (chosen == IntPtr.Zero)
        {
            if (!_hasCardRegion)
                AppLog.Warn("未找到任何任务栏窗口，等待下一轮轮询");
            return;
        }

        var trayRect = new PixelRect(chosenRect.Left, chosenRect.Top, chosenRect.Right, chosenRect.Bottom);

        // 锚点组件暂隐（暂停/切歌过渡）时保留上次选中的任务栏与卡片区域，避免卡片跳到别处
        if (widget is null && _hasCardRegion && _wasAnchorBased)
        {
            IsWidgetPresent = false;
        }
        else
        {
            _taskbarHwnd = chosen;
            _taskbarRect = chosenRect;
            _isPrimaryTaskbar = chosenPrimary;

            var card = widget is { } w
                ? ComputeAnchorCardRegion(trayRect, w, start) ?? ComputeFallbackRegion(trayRect, start)
                : ComputeIndependentRegion(chosen, trayRect) ?? ComputeFallbackRegion(trayRect, start);

            _wasAnchorBased = widget is not null;
            if (card is { } c)
            {
                _cardRegion = c;
                _hasCardRegion = true;
            }
            IsWidgetPresent = widget is not null;
        }

        // 签名：任务栏句柄 + 矩形 + 卡片区域 + 组件显隐 + 白名单 + 放置方向
        var signature = $"{_taskbarHwnd.ToInt64()}|{_taskbarRect.Left},{_taskbarRect.Top},{_taskbarRect.Right},{_taskbarRect.Bottom}|" +
                        (_hasCardRegion ? $"{_cardRegion.Left},{_cardRegion.Top},{_cardRegion.Right},{_cardRegion.Bottom}" : "-") + "|" +
                        IsWidgetPresent + "|" +
                        string.Join(",", _enabledKeys) + "|" +
                        _placementDirection;

        if (signature == _layoutSignature)
            return;

        _layoutSignature = signature;
        AppLog.Info($"任务栏定位更新: 任务栏=({_taskbarRect.Left},{_taskbarRect.Top},{_taskbarRect.Right},{_taskbarRect.Bottom}) " +
                    $"卡片={(_hasCardRegion ? $"({_cardRegion.Left},{_cardRegion.Top},{_cardRegion.Right},{_cardRegion.Bottom})" : "无")} " +
                    $"组件可见={IsWidgetPresent} 白名单=[{string.Join(",", _enabledKeys)}] 方向={_placementDirection}");
        TaskbarChanged?.Invoke();
    }

    /// <summary>按白名单顺序尝试锚点定位器（独立定位器不是锚点，跳过），返回第一个成功的组件矩形</summary>
    private PixelRect? DetectAnchorWidget(IntPtr trayHwnd, PixelRect trayRect)
    {
        foreach (var locator in _locators)
        {
            if (!_enabledKeys.Contains(locator.Key))
                continue;
            if (string.Equals(locator.Key, IndependentLocatorKey, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var rect = locator.Detect(trayHwnd, trayRect);
                if (rect is not null)
                    return rect;
            }
            catch (Exception ex)
            {
                AppLog.Error($"组件定位器 {locator.Name} 探测失败", ex);
            }
        }

        return null;
    }

    /// <summary>根据锚点组件矩形、放置方向与开始按钮计算卡片区域（屏幕物理像素）</summary>
    private PixelRect? ComputeAnchorCardRegion(PixelRect tray, PixelRect widget, PixelRect? start)
    {
        var placeRight = _placementDirection switch
        {
            "Right" => true,
            "Left" => false,
            // 自动：组件在任务栏左半区 → 贴右侧；右半区 → 贴左侧
            _ => (widget.Left + widget.Right) / 2 <= (tray.Left + tray.Right) / 2,
        };

        if (placeRight)
        {
            var left = widget.Right + CardGap;
            // 右界：优先开始按钮左侧；找不到则主屏回退中线（开始按钮居中）、副屏留出托盘空间
            var rightBound = start is { } s
                ? s.Left - CardGap
                : _isPrimaryTaskbar
                    ? tray.Left + tray.Width / 2
                    : tray.Right - 170;
            var right = Math.Min(left + MaxCardWidth, rightBound);
            return right - left >= MinCardWidth ? new PixelRect(left, tray.Top, right, tray.Bottom) : null;
        }

        var rightEdge = widget.Left - CardGap;
        // 左界：任务栏左缘 / 开始按钮右缘（避免盖住开始按钮）与最大宽度三者取最右
        var leftBound = Math.Max(tray.Left + CardGap, (start?.Right ?? tray.Left) + CardGap);
        var leftEdge = Math.Max(leftBound, rightEdge - MaxCardWidth);
        return rightEdge - leftEdge >= MinCardWidth ? new PixelRect(leftEdge, tray.Top, rightEdge, tray.Bottom) : null;
    }

    /// <summary>白名单启用独立定位时，用独立定位器计算卡片区域；否则返回 null</summary>
    private PixelRect? ComputeIndependentRegion(IntPtr trayHwnd, PixelRect trayRect)
    {
        if (!_enabledKeys.Contains(IndependentLocatorKey))
            return null;

        foreach (var locator in _locators)
        {
            if (!string.Equals(locator.Key, IndependentLocatorKey, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                return locator.DetectCardRegion(trayHwnd, trayRect);
            }
            catch (Exception ex)
            {
                AppLog.Error($"独立定位器 {locator.Name} 探测失败", ex);
            }
        }

        return null;
    }

    /// <summary>最后兜底：卡片占任务栏左缘到开始按钮（或按主/副屏规则）的可用区域</summary>
    private PixelRect ComputeFallbackRegion(PixelRect tray, PixelRect? start)
    {
        var left = tray.Left;
        var rightEdge = start is { } s
            ? s.Left - CardGap
            : _isPrimaryTaskbar
                ? tray.Left + tray.Width / 2
                : tray.Right - 170;

        var right = Math.Max(left + MinCardWidth, rightEdge);
        return new PixelRect(left, tray.Top, right, tray.Bottom);
    }

    /// <summary>枚举所有任务栏窗口，主任务栏排最前</summary>
    private static List<IntPtr> FindTaskbarHandles()
    {
        var result = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var cls = GetTaskbarClass(h);
            if (cls == PrimaryTaskbarClass || cls == SecondaryTaskbarClass)
                result.Add(h);
            return true;
        }, IntPtr.Zero);

        return result
            .OrderBy(h => GetTaskbarClass(h) == PrimaryTaskbarClass ? 0 : 1)
            .ToList();
    }

    private static string GetTaskbarClass(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        GetClassName(hwnd, className, 128);
        return className.ToString();
    }

    /// <summary>把当前选中的任务栏矩形换算成 DIP 坐标（相对虚拟屏幕左上角）</summary>
    public Rect GetTaskbarRectDips(Window window)
    {
        return ToDips(window, new PixelRect(_taskbarRect.Left, _taskbarRect.Top, _taskbarRect.Right, _taskbarRect.Bottom));
    }

    /// <summary>推荐卡片区域的 DIP 矩形，来自最近一次成功计算的缓存；没找到返回 null</summary>
    public Rect? GetCardRegionDips(Window window)
    {
        if (!_hasCardRegion)
            return null;

        var rect = ToDips(window, _cardRegion);
        AppLog.Debug($"卡片区域: {rect}");
        return rect;
    }

    private static Rect ToDips(Window window, PixelRect rect)
    {
        var transform = GetTransform(window);
        var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
        var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static Matrix GetTransform(Window window)
    {
        return PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
               ?? Matrix.Identity;
    }

    /// <summary>
    /// 通过 Win32 子窗口定位开始按钮（Shell_TrayWnd 下类名为 Start 的子窗口）。
    /// 副屏任务栏（Shell_SecondaryTrayWnd）没有开始按钮，返回 null，由调用方回退。
    /// </summary>
    private static PixelRect? DetectStartButtonRect(IntPtr trayHwnd)
    {
        if (trayHwnd == IntPtr.Zero)
            return null;

        var startHwnd = FindWindowEx(trayHwnd, IntPtr.Zero, "Start", null);
        if (startHwnd == IntPtr.Zero)
            return null;

        if (!GetWindowRect(startHwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return null;

        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnSystemEvent;
        SystemEvents.UserPreferenceChanged -= OnSystemEvent;
    }
}
