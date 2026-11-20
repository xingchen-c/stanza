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

    public static IReadOnlyList<TaskbarWindow> Enumerate(IntPtr preferredHandle)
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
                GetClass(hwnd) == PrimaryTaskbarClass));
        }

        return OrderForSelection(result, preferredHandle);
    }

    internal static IReadOnlyList<TaskbarWindow> OrderForSelection(
        IReadOnlyList<TaskbarWindow> taskbars, IntPtr preferredHandle)
        => taskbars.OrderByDescending(t => t.Handle == preferredHandle)
            .ThenByDescending(t => t.IsPrimary)
            .ThenBy(t => t.Rect.Left)
            .ThenBy(t => t.Rect.Top)
            .ThenBy(t => t.Handle.ToInt64())
            .ToArray();

    public static string GetClass(IntPtr hwnd)
    {
        var className = new StringBuilder(128);
        if (NativeMethods.GetClassName(hwnd, className, 128) == 0)
            return string.Empty;
        return className.ToString();
    }
}
