using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Text;
using System.Windows.Automation;

namespace FluentLyrics;

/// <summary>
/// 任务栏定位服务：
/// 读取主任务栏（Shell_TrayWnd）矩形并换算成 DIP 坐标；
/// 自动探测任务栏上第一个组件（FluentFlyout 音乐组件）的位置；
/// 任务栏移动 / 自动隐藏 / 组件显隐变化时触发 TaskbarChanged。
/// </summary>
public sealed class TaskbarService : IDisposable
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";

    // 任务栏上需要"排到右侧"的组件进程名列表，可随时扩展
    private static readonly string[] ComponentProcessNames = ["FluentFlyout"];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

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
    private string _componentSignature = string.Empty;

    /// <summary>是否拿到了有效的任务栏矩形</summary>
    public bool HasTaskbar => _taskbarRect.Width > 0 && _taskbarRect.Height > 0;

    /// <summary>任务栏矩形或组件位置变化时触发（已在 UI 线程）</summary>
    public event Action? TaskbarChanged;

    public TaskbarService()
    {
        // 兜底轮询：任务栏自动隐藏、被移动、组件显隐等场景没有可靠事件，1 秒一次足够
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

    /// <summary>立即重新读取任务栏与组件信息；有变化时触发 TaskbarChanged</summary>
    public void Refresh()
    {
        var rect = FindPrimaryTaskbarRect();
        var signature = BuildComponentSignature(rect);

        if (rect.Left == _taskbarRect.Left && rect.Top == _taskbarRect.Top &&
            rect.Right == _taskbarRect.Right && rect.Bottom == _taskbarRect.Bottom &&
            signature == _componentSignature)
            return;

        _taskbarRect = rect;
        _componentSignature = signature;
        TaskbarChanged?.Invoke();
    }

    /// <summary>把主任务栏矩形换算成 DIP 坐标（相对虚拟屏幕左上角）</summary>
    public Rect GetTaskbarRectDips(Window window)
    {
        var transform = GetTransform(window);
        var topLeft = transform.Transform(new Point(_taskbarRect.Left, _taskbarRect.Top));
        var bottomRight = transform.Transform(new Point(_taskbarRect.Right, _taskbarRect.Bottom));
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

    private static string BuildComponentSignature(RECT trayRect)
    {
        var parts = new List<string>();
        foreach (var hwnd in FindComponentWindows(trayRect))
        {
            if (GetWindowRect(hwnd, out var r))
                parts.Add($"{r.Left},{r.Top},{r.Right},{r.Bottom}");
        }

        return string.Join(";", parts);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnSystemEvent;
        SystemEvents.UserPreferenceChanged -= OnSystemEvent;
    }

    /// <summary>
    /// 读取 FluentFlyout 任务栏小组件的可见区域（DIP 矩形）。
    /// FluentFlyout 用 SetWindowRgn 把铺满任务栏的宿主窗口裁剪成组件形状，
    /// 所以 GetWindowRgn + GetRgnBox 拿到的就是组件真实位置 —— 这是我们的"自动锚点"。
    /// 没找到返回 null。
    /// </summary>
    public Rect? GetFluentFlyoutWidgetRectDips(Window window)
    {
        foreach (var hwnd in FindFluentFlyoutTaskbarWindows())
        {
            var rgn = CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (GetWindowRgn(hwnd, rgn) == 0) // ERROR：窗口没有设置裁剪区域
                    continue;
                if (GetRgnBox(rgn, out var box) == 0)
                    continue;

                // 区域坐标相对窗口左上角；窗口铺满任务栏，加上窗口屏幕原点即绝对坐标
                GetWindowRect(hwnd, out var winRect);
                var transform = GetTransform(window);
                var topLeft = transform.Transform(new Point(winRect.Left + box.Left, winRect.Top + box.Top));
                var bottomRight = transform.Transform(new Point(winRect.Left + box.Right, winRect.Top + box.Bottom));
                var rect = new Rect(topLeft, bottomRight);

                Debug.WriteLine($"[FluentLyrics] FluentFlyout 组件可见区域: {rect}");
                return rect;
            }
            finally
            {
                DeleteObject(rgn);
            }
        }

        return null;
    }

    private AutomationElement? _startButtonElement;

    /// <summary>
    /// 通过 UI Automation 定位任务栏开始按钮（AutomationId=StartButton），返回 DIP 矩形。
    /// 找不到返回 null；元素缓存，Explorer 重启后自动重新查询。
    /// </summary>
    public Rect? GetStartButtonRectDips(Window window)
    {
        var trayHwnd = FindWindow(PrimaryTaskbarClass, null);
        if (trayHwnd == IntPtr.Zero)
            return null;

        try
        {
            if (_startButtonElement is null)
            {
                var root = AutomationElement.FromHandle(trayHwnd);
                var findTask = Task.Run(() => root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton")));
                if (!findTask.Wait(1000))
                {
                    _startButtonElement = null;
                    return null;
                }

                _startButtonElement = findTask.Result;
                if (_startButtonElement is null)
                    return null;
            }

            var bounds = _startButtonElement.Current.BoundingRectangle;
            if (bounds.IsEmpty)
                return null;

            var transform = GetTransform(window);
            var topLeft = transform.Transform(new Point(bounds.Left, bounds.Top));
            var bottomRight = transform.Transform(new Point(bounds.Right, bounds.Bottom));
            var rect = new Rect(topLeft, bottomRight);
            Debug.WriteLine($"[FluentLyrics] 开始按钮矩形: {rect}");
            return rect;
        }
        catch
        {
            _startButtonElement = null; // 元素失效（Explorer 重启等），下次重新查询
            return null;
        }
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