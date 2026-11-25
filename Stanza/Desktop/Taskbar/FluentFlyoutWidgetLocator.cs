using System.Diagnostics;
using System.Text;
using Stanza.Interop;
using Stanza.Core.Models;

namespace Stanza.Desktop.Taskbar;

/// <summary>
///     FluentFlyout 任务栏小组件定位器：
///     FluentFlyout 用 SetWindowRgn 把铺满任务栏的宿主窗口裁剪成组件形状，
///     所以 GetWindowRgn + GetRgnBox 拿到的就是组件真实位置。
/// </summary>
public sealed class FluentFlyoutWidgetLocator : ITaskbarWidgetLocator
{
    // 任务栏上需要"排到右侧"的组件进程名列表，可随时扩展
    private static readonly string[] ComponentProcessNames = ["FluentFlyout"];

    /// <inheritdoc />
    public string Key => "FluentFlyout";

    /// <inheritdoc />
    public string Name => "FluentFlyout";

    /// <summary>读取 FluentFlyout 宿主窗口的区域裁剪，得到组件真实屏幕矩形。</summary>
    public PixelRect? Detect(IntPtr trayHwnd, PixelRect trayRect)
    {
        foreach (var hwnd in FindFluentFlyoutTaskbarWindows(trayHwnd))
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) // 组件被隐藏时视为不存在
                continue;

            var rgn = NativeMethods.CreateRectRgn(0, 0, 0, 0);
            try
            {
                if (NativeMethods.GetWindowRgn(hwnd, rgn) == 0) // ERROR：窗口没有设置裁剪区域
                    continue;
                if (NativeMethods.GetRgnBox(rgn, out var box) == 0)
                    continue;

                // 暂停/恢复/切歌的过渡瞬间，裁剪区域可能短暂铺满整个任务栏，
                // 组件卡片不可能那么宽，这种读数直接忽略，等下一轮恢复正常
                if (trayRect.Width > 0 && box.Right - box.Left > trayRect.Width / 2)
                    continue;

                // 区域坐标相对窗口左上角；窗口铺满任务栏，加上窗口屏幕原点即绝对坐标
                NativeMethods.GetWindowRect(hwnd, out var winRect);
                return new PixelRect(
                    winRect.Left + box.Left,
                    winRect.Top + box.Top,
                    winRect.Left + box.Right,
                    winRect.Top + box.Bottom);
            }
            finally
            {
                NativeMethods.DeleteObject(rgn);
            }
        }

        return null;
    }

    /// <summary>在任务栏子窗口中找 FluentFlyout 的宿主窗口（类名 HwndWrapper[FluentFlyout...）</summary>
    private static List<IntPtr> FindFluentFlyoutTaskbarWindows(IntPtr trayHwnd)
    {
        var result = new List<IntPtr>();
        var pids = new HashSet<int>(GetComponentProcessIds());
        if (pids.Count == 0 || trayHwnd == IntPtr.Zero)
            return result;

        NativeMethods.EnumChildWindows(trayHwnd, (h, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(h, out var pid) == 0)
                return true;
            if (!pids.Contains((int)pid))
                return true;

            var className = new StringBuilder(128);
            if (NativeMethods.GetClassName(h, className, 128) == 0)
                return true;
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

}
