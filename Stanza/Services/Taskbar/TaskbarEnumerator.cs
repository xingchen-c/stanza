using System.Runtime.InteropServices;
using System.Text;
using Stanza.Interop;
using Stanza.Models;

namespace Stanza.Services.Taskbar;

/// <summary>枚举系统任务栏窗口，并返回主/副屏标识。</summary>
internal static class TaskbarEnumerator
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const uint MonitorDefaultToNearest = 2;

    internal readonly record struct TaskbarWindow(
        IntPtr Handle,
        PixelRect Rect,
        bool IsPrimary,
        IntPtr Monitor = default);

    public static IReadOnlyList<TaskbarWindow> Enumerate(IntPtr preferredHandle, IntPtr preferredMonitor)
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            var cls = GetClass(h);
            if (cls == PrimaryTaskbarClass || cls == SecondaryTaskbarClass)
                handles.Add(h);
            return true;
        }, IntPtr.Zero);

        var result = new List<TaskbarWindow>();
        foreach (var hwnd in handles)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) ||
                rect.Width <= 0 ||
                rect.Height <= 0)
                continue;

            result.Add(new TaskbarWindow(
                hwnd,
                new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                GetClass(hwnd) == PrimaryTaskbarClass,
                NativeMethods.MonitorFromWindow(hwnd, MonitorDefaultToNearest)));
        }

        var monitorInfo = new NativeMethods.NativeMonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.NativeMonitorInfo>()
        };
        var monitorConnected = preferredMonitor != IntPtr.Zero &&
                               NativeMethods.GetMonitorInfo(preferredMonitor, ref monitorInfo);
        return OrderForSelection(result, preferredHandle, preferredMonitor, monitorConnected);
    }

    internal static IReadOnlyList<TaskbarWindow> OrderForSelection(
        IReadOnlyList<TaskbarWindow> taskbars, IntPtr preferredHandle,
        IntPtr preferredMonitor = default, bool preferredMonitorConnected = false)
    {
        // 开始菜单可能让该屏任务栏暂时不被枚举，不能据此改选其他显示器。
        if (preferredMonitorConnected && taskbars.All(t => t.Monitor != preferredMonitor))
            return [];

        return taskbars.OrderByDescending(t => preferredMonitor != IntPtr.Zero && t.Monitor == preferredMonitor)
            .ThenByDescending(t => t.Handle == preferredHandle)
            .ThenByDescending(t => t.IsPrimary)
            .ThenBy(t => t.Rect.Left)
            .ThenBy(t => t.Rect.Top)
            .ThenBy(t => t.Handle.ToInt64())
            .ToArray();
    }

    public static string GetClass(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(hwnd, className, 128) == 0)
            return string.Empty;
        return className.ToString();
    }
}
