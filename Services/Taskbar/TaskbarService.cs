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
/// 优先选择探到组件（FluentFlyout）的那条；组件暂隐时保留上次选中的任务栏与位置。
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

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

    // 当前选中的任务栏（组件所在的那条；组件暂隐时保留上次）
    private IntPtr _taskbarHwnd;
    private RECT _taskbarRect;
    private bool _isPrimaryTaskbar = true;

    // 探测结果缓存（屏幕物理像素）：单次探测失败保留上次结果，避免卡片跳位
    private PixelRect _widgetRect;
    private bool _hasWidgetRect;
    private PixelRect _startButtonRect;
    private bool _hasStartButtonRect;

    /// <summary>是否拿到了有效的任务栏矩形</summary>
    public bool HasTaskbar => _taskbarRect.Width > 0 && _taskbarRect.Height > 0;

    /// <summary>当前选中的是否主任务栏（副屏任务栏没有开始按钮，定位回退策略不同）</summary>
    public bool IsPrimaryTaskbar => _isPrimaryTaskbar;

    /// <summary>本次探测组件是否真的在显示（不读粘性缓存，暂停播放组件隐藏时为 false）</summary>
    public bool IsWidgetPresent { get; private set; }

    /// <summary>任务栏矩形、组件可见区域或开始按钮位置变化时触发（已在 UI 线程）</summary>
    public event Action? TaskbarChanged;

    /// <summary>默认使用 FluentFlyout 组件定位器</summary>
    public TaskbarService()
        : this([new FluentFlyoutWidgetLocator()])
    {
    }

    /// <summary>可注入自定义组件定位器（按顺序尝试，第一个成功的胜出）</summary>
    public TaskbarService(params ITaskbarWidgetLocator[] locators)
    {
        _locators = locators.Length > 0 ? locators : [new FluentFlyoutWidgetLocator()];

        // 兜底轮询：任务栏自动隐藏、被移动、组件显隐、显示器变化等场景没有可靠事件，1 秒一次足够
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollTimer.Tick += (_, _) => Refresh();
        _pollTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnSystemEvent;
        SystemEvents.UserPreferenceChanged += OnSystemEvent;

        Refresh();
    }

    private void OnSystemEvent(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(new Action(Refresh));
    }

    /// <summary>立即重新读取任务栏、组件区域与开始按钮位置；有变化时触发 TaskbarChanged</summary>
    public void Refresh()
    {
        // 多显示器：枚举所有任务栏，优先选"探到组件"的那条；找不到时用第一条（主任务栏）兜底
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
            var detected = DetectWidget(hwnd, tray);
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
            if (!_hasWidgetRect && !_hasStartButtonRect)
                AppLog.Warn("未找到任何任务栏窗口，等待下一轮轮询");
            return;
        }

        // 组件暂隐（暂停/切歌过渡）时保留上次选中的任务栏与位置，避免卡片跳到别处
        if (widget is null && _hasWidgetRect)
        {
            IsWidgetPresent = false;
        }
        else
        {
            _taskbarHwnd = chosen;
            _taskbarRect = chosenRect;
            _isPrimaryTaskbar = chosenPrimary;

            if (widget is { } w)
            {
                _widgetRect = w;
                _hasWidgetRect = true;
            }

            if (start is { } s)
            {
                _startButtonRect = s;
                _hasStartButtonRect = true;
            }

            IsWidgetPresent = widget is not null;
        }

        // 签名：任务栏句柄 + 矩形 + 组件区域 + 开始按钮 + 组件显隐
        var signature = $"{_taskbarHwnd.ToInt64()}|{_taskbarRect.Left},{_taskbarRect.Top},{_taskbarRect.Right},{_taskbarRect.Bottom}|" +
                        (_hasWidgetRect ? $"{_widgetRect.Left},{_widgetRect.Top},{_widgetRect.Right},{_widgetRect.Bottom}" : "-") + "|" +
                        (_hasStartButtonRect ? $"{_startButtonRect.Left},{_startButtonRect.Top},{_startButtonRect.Right},{_startButtonRect.Bottom}" : "-") + "|" +
                        IsWidgetPresent;

        if (signature == _layoutSignature)
            return;

        _layoutSignature = signature;
        AppLog.Info($"任务栏定位更新: 任务栏=({_taskbarRect.Left},{_taskbarRect.Top},{_taskbarRect.Right},{_taskbarRect.Bottom}) " +
                    $"组件={(_hasWidgetRect ? $"({_widgetRect.Left},{_widgetRect.Top},{_widgetRect.Right},{_widgetRect.Bottom})" : "无")} " +
                    $"开始按钮={(_hasStartButtonRect ? $"({_startButtonRect.Left},{_startButtonRect.Top},{_startButtonRect.Right},{_startButtonRect.Bottom})" : "无")} " +
                    $"组件可见={IsWidgetPresent}");
        TaskbarChanged?.Invoke();
    }

    /// <summary>按顺序尝试各定位器，返回第一个成功的组件矩形</summary>
    private PixelRect? DetectWidget(IntPtr trayHwnd, PixelRect trayRect)
    {
        foreach (var locator in _locators)
        {
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

    /// <summary>组件（卡片应紧贴其右侧）的 DIP 矩形，来自最近一次成功探测的缓存；没找到返回 null</summary>
    public Rect? GetWidgetRectDips(Window window)
    {
        if (!_hasWidgetRect)
            return null;

        var rect = ToDips(window, _widgetRect);
        AppLog.Debug($"组件可见区域: {rect}");
        return rect;
    }

    /// <summary>开始按钮的 DIP 矩形，来自最近一次成功探测的缓存；找不到返回 null</summary>
    public Rect? GetStartButtonRectDips(Window window)
    {
        if (!_hasStartButtonRect)
            return null;

        var rect = ToDips(window, _startButtonRect);
        AppLog.Debug($"开始按钮矩形: {rect}");
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
