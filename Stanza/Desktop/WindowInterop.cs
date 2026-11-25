using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Stanza.Interop;

namespace Stanza.Desktop;

/// <summary>主窗口的窗口样式与光标坐标互操作。</summary>
internal sealed class WindowInterop
{
    private const int GwlExstyle = -20;
    private const long WsExNoactivate = 0x08000000;
    private const long WsExTransparent = 0x00000020;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    public void ApplyStyles(
        Window window,
        bool adjustingPosition,
        bool clickThrough,
        bool pointerInMediaRegion)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var currentStyle = NativeMethods.GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        var exStyle = CalculateExtendedStyle(currentStyle, adjustingPosition, clickThrough, pointerInMediaRegion);
        if (exStyle == currentStyle)
            return;

        NativeMethods.SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(exStyle));
        NativeMethods.SetWindowPos(
            hwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate);
    }

    internal static long CalculateExtendedStyle(
        long currentStyle, bool adjustingPosition, bool clickThrough, bool pointerInMediaRegion)
    {
        var style = currentStyle | WsExNoactivate;
        // 分层窗口用原生整窗样式穿透；HTTRANSPARENT 只在同线程窗口间传递，不能代替此处。
        return clickThrough && !adjustingPosition && !pointerInMediaRegion
            ? style | WsExTransparent
            : style & ~WsExTransparent;
    }

    public Point GetCursorPositionPixels()
    {
        NativeMethods.GetCursorPos(out var point);
        return new Point(point.X, point.Y);
    }

    public Point GetCursorPositionDips(Visual visual)
    {
        var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice
                        ?? Matrix.Identity;
        return transform.Transform(GetCursorPositionPixels());
    }
}
