using System.Text;
using Stanza.Interop;
using Stanza.Models;

namespace Stanza.Services.Desktop;

/// <summary>
///     前台全屏检测器：判断前景窗口是否覆盖了任务栏。
///     只负责识别"全屏应用"，不修改窗口状态或显隐策略。
/// </summary>
internal sealed class ForegroundFullscreenDetector
{
    private static readonly string[] ShellWindowClasses =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"];

    private int _lastDiagTick = -5000;

    /// <summary>
    ///     前台窗口覆盖任务栏面积达到 95% 时视为全屏；系统外壳窗口和当前进程窗口始终排除。
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

        if (IsShellWindow(hwnd))
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        var coverW = Math.Min(rect.Right, tray.Right) - Math.Max(rect.Left, tray.Left);
        var coverH = Math.Min(rect.Bottom, tray.Bottom) - Math.Max(rect.Top, tray.Top);
        var covered = coverW >= tray.Width * 0.95 && coverH >= tray.Height * 0.95;

        var now = Environment.TickCount;
        if (now - _lastDiagTick > 5000)
        {
            _lastDiagTick = now;
            var name = new StringBuilder(128);
            if (NativeMethods.GetClassName(hwnd, name, 128) == 0)
                name.Clear();
            AppLog.Debug(
                $"全屏检测: 前台='{name}' 窗口=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) " +
                $"任务栏=({tray.Left},{tray.Top},{tray.Right},{tray.Bottom}) 覆盖={covered}");
        }

        return covered;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(hwnd, className, 128) == 0)
            return false;
        return ShellWindowClasses.Contains(className.ToString());
    }
}
