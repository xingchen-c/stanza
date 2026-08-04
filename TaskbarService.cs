using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Text;

namespace FluentLyrics;

/// <summary>
/// 任务栏定位服务：
/// 读取主任务栏（Shell_TrayWnd）矩形并换算成 DIP 坐标；
/// 自动探测任务栏上 FluentFlyout 组件的可见区域和开始按钮位置；
/// 任务栏移动 / 自动隐藏 / 组件显隐 / 图标数量变化时触发 TaskbarChanged。
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";

    // 任务栏上需要"排到右侧"的组件进程名列表，可随时扩展
    private static readonly string[] ComponentProcessNames = ["FluentFlyout"];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    private static extern int GetRgnBox(IntPtr hRgn, out RECT rect);

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
    private RECT _taskbarRect;
    private string _layoutSignature = string.Empty;

    // 探测结果缓存（屏幕物理像素）：
    // 单次探测失败时保留上次结果，避免卡片瞬间跳到兜底位置盖住 FluentFlyout 组件
    private RECT _widgetRect;
    private bool _hasWidgetRect;
    private RECT _startButtonRect;
    private bool _hasStartButtonRect;

    /// <summary>是否拿到了有效的任务栏矩形</summary>
    public bool HasTaskbar => _taskbarRect.Width > 0 && _taskbarRect.Height > 0;

    /// <summary>FluentFlyout 进程是否在运行</summary>
    public bool HasComponentProcess { get; private set; }

    /// <summary>本次探测组件是否真的在显示（不读粘性缓存，暂停播放组件隐藏时为 false）</summary>
    public bool IsWidgetPresent { get; private set; }


    /// <summary>任务栏矩形、组件可见区域或开始按钮位置变化时触发（已在 UI 线程）</summary>
    public event Action? TaskbarChanged;

    public TaskbarService()
    {
        // 兜底轮询：任务栏自动隐藏、被移动、组件显隐、图标增减等场景没有可靠事件，1 秒一次足够
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
        var rect = FindPrimaryTaskbarRect();
        UpdateWidgetCache();
        UpdateStartButtonCache();

        // 签名同时覆盖：任务栏矩形、组件可见区域（切歌会改变组件宽度）、
        // 开始按钮位置（任务栏图标增减会让居中图标组移动）
        var signature = $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}|" +
                        (_hasWidgetRect ? $"{_widgetRect.Left},{_widgetRect.Top},{_widgetRect.Right},{_widgetRect.Bottom}" : "-") + "|" +
                        (_hasStartButtonRect ? $"{_startButtonRect.Left},{_startButtonRect.Top},{_startButtonRect.Right},{_startButtonRect.Bottom}" : "-") + "|" +
                        IsWidgetPresent;

        if (signature == _layoutSignature)
            return;

        _layoutSignature = signature;
        _taskbarRect = rect;
        TaskbarChanged?.Invoke();
    }

    /// <summary>探测 FluentFlyout 组件可见区域；偶发失败保留旧值，进程退出才清空</summary>
    private void UpdateWidgetCache()
    {
        var rect = DetectWidgetRegionRect();
        HasComponentProcess = GetComponentProcessIds().Any();
        IsWidgetPresent = rect is not null;

        if (rect is { } r)
        {
            _widgetRect = r;
            _hasWidgetRect = true;
        }
        else if (!HasComponentProcess)
        {
            // FluentFlyout 没在运行，组件确实不存在
            _hasWidgetRect = false;
        }
    }


    /// <summary>探测开始按钮位置；偶发失败保留旧值（元素失效时下轮自动重新查询）</summary>
    private void UpdateStartButtonCache()
    {
        var rect = DetectStartButtonRect();
        if (rect is { } r)
        {
            _startButtonRect = r;
            _hasStartButtonRect = true;
        }
    }

    /// <summary>把主任务栏矩形换算成 DIP 坐标（相对虚拟屏幕左上角）</summary>
    public Rect GetTaskbarRectDips(Window window)
    {
        return ToDips(window, _taskbarRect);
    }

    private static Rect ToDips(Window window, RECT rect)
    {
        var transform = GetTransform(window);
        var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
        var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>任务栏上最左侧组件（FluentFlyout 音乐组件）的 DIP 矩形；没找到返回 null</summary>
    public Rect? GetFirstComponentRectDips(Window window)
    {
        if (_taskbarRect.Width <= 0 || _taskbarRect.Height <= 0)
            return null;

        var transform = GetTransform(window);
        var rects = new List<(Rect Rect, string Class)>();

        foreach (var hwnd in FindComponentWindows(_taskbarRect))
        {
            if (!GetWindowRect(hwnd, out var r))
                continue;
            var className = new StringBuilder(128);
            GetClassName(hwnd, className, 128);
            var topLeft = transform.Transform(new Point(r.Left, r.Top));
            var bottomRight = transform.Transform(new Point(r.Right, r.Bottom));
            var dips = new Rect(topLeft, bottomRight);
            rects.Add((dips, className.ToString()));
            Debug.WriteLine($"[FluentLyrics] 候选组件: 类名={className} 矩形={dips}");
        }

        // 铺满任务栏的宿主窗口不算组件；优先取"明显窄于任务栏"的最左窗口
        var taskbarWidth = GetTaskbarRectDips(window).Width;
        var compact = rects.Where(x => x.Rect.Width <= taskbarWidth * 0.7).ToList();
        var pool = compact.Count > 0 ? compact : rects;

        return pool.Count > 0 ? pool.OrderBy(x => x.Rect.Left).First().Rect : null;
    }

    private static Matrix GetTransform(Window window)
    {
        return PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
               ?? Matrix.Identity;
    }

    private static RECT FindPrimaryTaskbarRect()
    {
        var hwnd = FindWindow(PrimaryTaskbarClass, null);
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
            return default;
        return rect;
    }

    private static List<IntPtr> FindComponentWindows(RECT trayRect)
    {
        var result = new List<IntPtr>();
        var pids = new HashSet<int>(GetComponentProcessIds());
        if (pids.Count == 0)
            return result;

        var region = trayRect;
        region.Left -= 8;
        region.Top -= 8;
        region.Right += 8;
        region.Bottom += 8;

        // 1. FluentFlyout 的顶层窗口
        EnumWindows((h, _) =>
        {
            if (IsComponentWindow(h, region, pids))
                result.Add(h);
            return true;
        }, IntPtr.Zero);

        // 2. 任务栏的子窗口（组件画在任务栏内部的情况）
        var trayHwnd = FindWindow(PrimaryTaskbarClass, null);
        if (trayHwnd != IntPtr.Zero)
        {
            EnumChildWindows(trayHwnd, (h, _) =>
            {
                if (IsComponentWindow(h, region, pids) && !result.Contains(h))
                    result.Add(h);
                return true;
            }, IntPtr.Zero);
        }

        // 3. FluentFlyout 顶层窗口的子窗口（宿主铺满任务栏时，真正的组件是子窗口）
        var topWindows = result.ToList();
        foreach (var top in topWindows)
        {
            EnumChildWindows(top, (h, _) =>
            {
                if (IsComponentWindow(h, region, pids) && !result.Contains(h))
                    result.Add(h);
                return true;
            }, IntPtr.Zero);
        }

        return result;
    }

    private static bool IsComponentWindow(IntPtr hwnd, RECT region, HashSet<int> pids)
    {
        if (!IsWindowVisible(hwnd))
            return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (!pids.Contains((int)pid))
            return false;
        GetWindowRect(hwnd, out var rect);
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return false;

        // 只认中心点落在任务栏区域附近的窗口，排除全屏 / 超大覆盖窗口
        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        return centerX >= region.Left && centerX < region.Right &&
               centerY >= region.Top && centerY < region.Bottom;
    }

    private static IEnumerable<int> GetComponentProcessIds()
    {
        var ids = new List<int>();
        try
        {
            foreach (var name in ComponentProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                    ids.Add(process.Id);
            }
        }
        catch
        {
            // 进程枚举失败时忽略，返回已收集到的 ID
        }

        return ids;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnSystemEvent;
        SystemEvents.UserPreferenceChanged -= OnSystemEvent;
    }

    /// <summary>
    /// FluentFlyout 任务栏小组件的可见区域（DIP 矩形），来自最近一次成功探测的缓存。
    /// 没找到返回 null。
    /// </summary>
    public Rect? GetFluentFlyoutWidgetRectDips(Window window)
    {
        if (!_hasWidgetRect)
            return null;

        var rect = ToDips(window, _widgetRect);
        Debug.WriteLine($"[FluentLyrics] FluentFlyout 组件可见区域: {rect}");
        return rect;
    }

    /// <summary>
    /// 读取 FluentFlyout 任务栏小组件的可见区域（屏幕物理像素）。
    /// FluentFlyout 用 SetWindowRgn 把铺满任务栏的宿主窗口裁剪成组件形状，
    /// 所以 GetWindowRgn + GetRgnBox 拿到的就是组件真实位置 —— 这是我们的"自动锚点"。
    /// 没找到返回 null。
    /// </summary>
    private static RECT? DetectWidgetRegionRect()
    {
        var trayRect = FindPrimaryTaskbarRect();

        foreach (var hwnd in FindFluentFlyoutTaskbarWindows())
        {
            if (!IsWindowVisible(hwnd)) // 组件被隐藏时视为不存在
                continue;

            var rgn = CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (GetWindowRgn(hwnd, rgn) == 0) // ERROR：窗口没有设置裁剪区域
                    continue;
                if (GetRgnBox(rgn, out var box) == 0)
                    continue;

                // 暂停/恢复/切歌的过渡瞬间，裁剪区域可能短暂铺满整个任务栏，
                // 组件卡片不可能那么宽，这种读数直接忽略，等下一轮恢复正常
                if (trayRect.Width > 0 && box.Right - box.Left > trayRect.Width / 2)
                    continue;

                // 区域坐标相对窗口左上角；窗口铺满任务栏，加上窗口屏幕原点即绝对坐标
                GetWindowRect(hwnd, out var winRect);
                return new RECT
                {
                    Left = winRect.Left + box.Left,
                    Top = winRect.Top + box.Top,
                    Right = winRect.Left + box.Right,
                    Bottom = winRect.Top + box.Bottom,
                };
            }
            finally
            {
                DeleteObject(rgn);
            }
        }

        return null;
    }

    /// <summary>
    /// 开始按钮的 DIP 矩形，来自最近一次成功探测的缓存。找不到返回 null。
    /// </summary>
    public Rect? GetStartButtonRectDips(Window window)
    {
        if (!_hasStartButtonRect)
            return null;

        var rect = ToDips(window, _startButtonRect);
        Debug.WriteLine($"[FluentLyrics] 开始按钮矩形: {rect}");
        return rect;
    }

    /// <summary>
    /// 通过 Win32 子窗口定位开始按钮（Shell_TrayWnd 下类名为 Start 的子窗口），返回屏幕物理像素矩形。
    /// 替代原 UI Automation 方案：UIA 客户端会让 explorer 一直保持无障碍树运转，拖慢整个任务栏。
    /// 找不到返回 null。
    /// </summary>
    private static RECT? DetectStartButtonRect()
    {
        var trayHwnd = FindWindow(PrimaryTaskbarClass, null);
        if (trayHwnd == IntPtr.Zero)
            return null;

        var startHwnd = FindWindowEx(trayHwnd, IntPtr.Zero, "Start", null);
        if (startHwnd == IntPtr.Zero)
            return null;

        if (!GetWindowRect(startHwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return null;

        return rect;
    }

    /// <summary>在任务栏子窗口中找 FluentFlyout 的宿主窗口（类名 HwndWrapper[FluentFlyout...）</summary>
    private static List<IntPtr> FindFluentFlyoutTaskbarWindows()
    {
        var result = new List<IntPtr>();
        var pids = new HashSet<int>(GetComponentProcessIds());
        if (pids.Count == 0)
            return result;

        var trayHwnd = FindWindow(PrimaryTaskbarClass, null);
        if (trayHwnd == IntPtr.Zero)
            return result;

        EnumChildWindows(trayHwnd, (h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains((int)pid))
                return true;

            var className = new StringBuilder(128);
            GetClassName(h, className, 128);
            if (className.ToString().StartsWith("HwndWrapper[FluentFlyout", StringComparison.OrdinalIgnoreCase))
                result.Add(h);
            return true;
        }, IntPtr.Zero);

        return result;
    }
}