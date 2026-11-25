using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Stanza.Interop;
using Stanza.Core.Models;

namespace Stanza.Desktop;

/// <summary>
///     前台全屏检测器：判断应用是否覆盖卡片所在的整块显示器。
///     只负责识别"全屏应用"，不修改窗口状态或显隐策略。
/// </summary>
internal sealed class ForegroundFullscreenDetector
{
    private const int DwmExtendedFrameBounds = 9;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly string[] ShellWindowClasses =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"];
    private static readonly string[] ShellProcessNames =
        ["StartMenuExperienceHost", "SearchHost", "ShellExperienceHost"];

    private int _lastDiagTick = -5000;

    /// <summary>
    ///     只检查当前任务栏所在显示器；系统外壳窗口和当前进程窗口始终排除。
    /// </summary>
    public bool IsForegroundFullscreen(PixelRect? taskbarRect)
    {
        if (taskbarRect is not { } tray)
            return false;

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd))
            return false;

        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0)
            return false;
        if (pid == (uint)Environment.ProcessId)
            return false;

        var trayRect = new NativeMethods.NativeRect
        {
            Left = tray.Left, Top = tray.Top, Right = tray.Right, Bottom = tray.Bottom
        };
        var monitor = NativeMethods.MonitorFromRect(ref trayRect, MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.NativeMonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.NativeMonitorInfo>()
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        // DWM 边界不含不可见的缩放边框，避免普通最大化窗口被多算成全屏。
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd, DwmExtendedFrameBounds, out var rect, Marshal.SizeOf<NativeMethods.NativeRect>()) != 0 &&
            !NativeMethods.GetWindowRect(hwnd, out rect))
            return false;
        if (rect.Width <= 0 || rect.Height <= 0)
            return false;

        var name = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, name, name.Capacity);
        var processName = GetProcessName(pid);
        var screen = monitorInfo.Monitor;
        var covered = IsFullscreenWindow(
            new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            new PixelRect(screen.Left, screen.Top, screen.Right, screen.Bottom),
            name.ToString(), processName);

        var now = Environment.TickCount;
        if (now - _lastDiagTick > 5000)
        {
            _lastDiagTick = now;
            AppLog.Debug(
                $"全屏检测: 进程='{processName}' 前台='{name}' 窗口=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) " +
                $"显示器=({screen.Left},{screen.Top},{screen.Right},{screen.Bottom}) 全屏={covered}");
        }

        return covered;
    }

    internal static bool IsFullscreenWindow(
        PixelRect window, PixelRect monitor, string windowClass, string? processName)
    {
        if (ShellWindowClasses.Contains(windowClass) ||
            ShellProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
            return false;

        // Explorer 的文件窗口可用 F11 全屏，其余窗口属于桌面、任务栏或外壳浮层。
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) &&
            windowClass is not ("CabinetWClass" or "ExploreWClass"))
            return false;

        return window.Left <= monitor.Left && window.Top <= monitor.Top &&
               window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
    }

    private static string? GetProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
