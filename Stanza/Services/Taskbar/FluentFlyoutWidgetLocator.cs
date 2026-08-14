using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Stanza.Models;

namespace Stanza.Services.Taskbar;

/// <summary>
///     FluentFlyout 任务栏小组件定位器：
///     FluentFlyout 用 SetWindowRgn 把铺满任务栏的宿主窗口裁剪成组件形状，
///     所以 GetWindowRgn + GetRgnBox 拿到的就是组件真实位置。
/// </summary>
public sealed class FluentFlyoutWidgetLocator : ITaskbarWidgetLocator
{
    // 任务栏上需要"排到右侧"的组件进程名列表，可随时扩展
    private static readonly string[] ComponentProcessNames = ["FluentFlyout"];

    public string Key => "FluentFlyout";

    public string Name => "FluentFlyout";

    public PixelRect? Detect(IntPtr trayHwnd, PixelRect trayRect)
    {
        foreach (var hwnd in FindFluentFlyoutTaskbarWindows(trayHwnd))
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
                return new PixelRect(
                    winRect.Left + box.Left,
                    winRect.Top + box.Top,
                    winRect.Left + box.Right,
                    winRect.Top + box.Bottom);
            }
            finally
            {
                DeleteObject(rgn);
            }
        }

        return null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    private static extern int GetRgnBox(IntPtr hRgn, out Rect rect);

    /// <summary>在任务栏子窗口中找 FluentFlyout 的宿主窗口（类名 HwndWrapper[FluentFlyout...）</summary>
    private static List<IntPtr> FindFluentFlyoutTaskbarWindows(IntPtr trayHwnd)
    {
        var result = new List<IntPtr>();
        var pids = new HashSet<int>(GetComponentProcessIds());
        if (pids.Count == 0 || trayHwnd == IntPtr.Zero)
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

    private static IEnumerable<int> GetComponentProcessIds()
    {
        var ids = new List<int>();
        try
        {
            foreach (var name in ComponentProcessNames)
            foreach (var process in Process.GetProcessesByName(name))
                ids.Add(process.Id);
        }
        catch
        {
            // 进程枚举失败时忽略，返回已收集到的 ID
        }

        return ids;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}