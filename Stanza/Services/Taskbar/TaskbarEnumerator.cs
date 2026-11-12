using System.Text;
using Stanza.Interop;
using Stanza.Models;

namespace Stanza.Services.Taskbar;

/// <summary>枚举系统任务栏窗口，并返回主/副屏标识。</summary>
internal static class TaskbarEnumerator
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    internal readonly record struct TaskbarWindow(
        IntPtr Handle,
        PixelRect Rect,
        bool IsPrimary);

    public static IReadOnlyList<TaskbarWindow> Enumerate()
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
        foreach (var hwnd in handles
                     .OrderBy(h => GetClass(h) == PrimaryTaskbarClass ? 0 : 1))
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) ||
                rect.Width <= 0 ||
                rect.Height <= 0)
                continue;

            result.Add(new TaskbarWindow(
                hwnd,
                new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                GetClass(hwnd) == PrimaryTaskbarClass));
        }

        return result;
    }

    public static string GetClass(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(hwnd, className, 128) == 0)
            return string.Empty;
        return className.ToString();
    }
}
