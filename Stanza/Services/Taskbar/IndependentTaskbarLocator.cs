using System.Runtime.InteropServices;
using Stanza.Models;

namespace Stanza.Services.Taskbar;

/// <summary>
///     独立任务栏定位器（不依赖 FluentFlyout 等外部组件）：
///     不提供组件锚点（Detect 恒为 null），而是直接给出卡片推荐区域 DetectCardRegion：
///     - 开始按钮居中：卡片默认左对齐（贴任务栏左缘，右缘不越过开始按钮）；
///     - 开始按钮居左 / 副屏（无开始按钮）：卡片默认水平居中，避开左侧图标区与右侧托盘。
///     宽度上限 600px、下限 120px；与目标区域统一留 8px 间隙。
/// </summary>
public sealed class IndependentTaskbarLocator : ITaskbarWidgetLocator
{
    private const int CardRightGap = 8;
    private const int MaxCardWidth = 600;
    private const int MinCardWidth = 120;
    /// <inheritdoc />
    public string Key => "Independent";

    /// <inheritdoc />
    public string Name => "独立定位";

    /// <summary>独立定位不依赖外部组件锚点，因此固定返回 null。</summary>
    public PixelRect? Detect(IntPtr trayHwnd, PixelRect trayRect)
    {
        return null;
    }

    /// <summary>根据开始按钮和托盘区域计算卡片推荐矩形。</summary>
    public PixelRect? DetectCardRegion(IntPtr trayHwnd, PixelRect trayRect)
    {
        if (trayHwnd == IntPtr.Zero || trayRect.Width <= 0 || trayRect.Height <= 0)
            return null;

        var start = GetChildRect(trayHwnd, "Start");
        var notify = GetChildRect(trayHwnd, "TrayNotifyWnd");

        // 开始按钮居中 → 卡片默认左对齐：从任务栏左缘开始，最多 600px，右缘不越过开始按钮
        var centered = start is { } s &&
                       Math.Abs((s.Left + s.Right) / 2 - (trayRect.Left + trayRect.Right) / 2) <= trayRect.Width / 4;
        if (start is { } startRect && centered)
        {
            var left = trayRect.Left + CardRightGap;
            var right = Math.Min(left + MaxCardWidth, startRect.Left - CardRightGap);
            return right - left >= MinCardWidth
                ? new PixelRect(left, trayRect.Top, right, trayRect.Bottom)
                : null;
        }

        // 开始按钮居左 / 副屏（无开始按钮）→ 卡片默认水平居中：
        // 左界避开开始按钮（若有），右界避开托盘，宽度不超过 600px
        var leftBound = Math.Max(trayRect.Left + CardRightGap,
            start is { } st ? st.Right + CardRightGap : trayRect.Left + CardRightGap);
        var rightBound = Math.Min(trayRect.Right - CardRightGap,
            notify is { } n ? n.Left - CardRightGap : trayRect.Right - CardRightGap);
        var width = Math.Min(MaxCardWidth, rightBound - leftBound);
        if (width < MinCardWidth)
            return null; // 空间太小放不下卡片，交给上层回退

        var center = (trayRect.Left + trayRect.Right) / 2;
        var cardLeft = Math.Clamp(center - width / 2, leftBound, rightBound - width);
        return new PixelRect(cardLeft, trayRect.Top, cardLeft + width, trayRect.Bottom);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    private static PixelRect? GetChildRect(IntPtr parent, string className)
    {
        var h = FindWindowEx(parent, IntPtr.Zero, className, null);
        if (h == IntPtr.Zero || !GetWindowRect(h, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return null;
        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
