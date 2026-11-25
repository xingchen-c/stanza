using Stanza.Interop;
using Stanza.Core.Models;

namespace Stanza.Desktop.Taskbar;

/// <summary>根据任务栏、组件和开始按钮计算卡片区域，不持有运行状态。</summary>
internal static class TaskbarLayoutCalculator
{
    private const string IndependentLocatorKey = "Independent";
    private const int CardGap = 8;
    private const int MaxCardWidth = 600;
    private const int MinCardWidth = 120;

    /// <summary>
    ///     按白名单顺序探测外部锚点组件。输入/输出全部是屏幕物理像素，独立定位器不在此处理。
    /// </summary>
    public static PixelRect? DetectAnchorWidget(
        IEnumerable<ITaskbarWidgetLocator> locators,
        IntPtr trayHwnd,
        PixelRect trayRect,
        HashSet<string> enabledKeys)
    {
        foreach (var locator in locators)
        {
            if (!enabledKeys.Contains(locator.Key))
                continue;
            if (string.Equals(locator.Key, IndependentLocatorKey, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (locator.Detect(trayHwnd, trayRect) is { } rect)
                    return rect;
            }
            catch (Exception ex)
            {
                AppLog.Error($"组件定位器 {locator.Name} 探测失败", ex);
            }
        }

        return null;
    }

    /// <summary>
    ///     计算不依赖外部组件的独立定位区域；输入/输出全部是屏幕物理像素。
    /// </summary>
    public static PixelRect? ComputeIndependentRegion(
        IEnumerable<ITaskbarWidgetLocator> locators,
        IntPtr trayHwnd,
        PixelRect trayRect,
        HashSet<string> enabledKeys)
    {
        if (!enabledKeys.Contains(IndependentLocatorKey))
            return null;

        foreach (var locator in locators)
        {
            if (!string.Equals(locator.Key, IndependentLocatorKey, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                return locator.DetectCardRegion(trayHwnd, trayRect);
            }
            catch (Exception ex)
            {
                AppLog.Error($"独立定位器 {locator.Name} 探测失败", ex);
            }
        }

        return null;
    }

    /// <summary>
    ///     根据锚点组件、开始按钮和放置方向计算卡片区域，全程使用屏幕物理像素。
    ///     自动方向：组件在任务栏左半区贴右侧，右半区贴左侧。
    /// </summary>
    public static PixelRect? ComputeAnchorCardRegion(
        PixelRect tray,
        PixelRect widget,
        PixelRect? start,
        string placement,
        bool isPrimary)
    {
        var placeRight = placement switch
        {
            "Right" => true,
            "Left" => false,
            _ => (widget.Left + widget.Right) / 2 <= (tray.Left + tray.Right) / 2
        };

        if (placeRight)
        {
            // 右界优先开始按钮左侧；主屏无开始按钮时取任务栏中点，副屏留出托盘空间。
            var left = widget.Right + CardGap;
            var rightBound = start is { } s
                ? s.Left - CardGap
                : isPrimary
                    ? tray.Left + tray.Width / 2
                    : tray.Right - 170;
            var right = Math.Min(left + MaxCardWidth, rightBound);
            return right - left >= MinCardWidth
                ? new PixelRect(left, tray.Top, right, tray.Bottom)
                : null;
        }

        // 左界：任务栏左边界、开始按钮右侧两者取最右。
        var rightEdge = widget.Left - CardGap;
        var leftBound = Math.Max(tray.Left + CardGap, (start?.Right ?? tray.Left) + CardGap);
        var leftEdge = Math.Max(leftBound, rightEdge - MaxCardWidth);
        return rightEdge - leftEdge >= MinCardWidth
            ? new PixelRect(leftEdge, tray.Top, rightEdge, tray.Bottom)
            : null;
    }

    /// <summary>
    ///     锚点和独立定位都失败时，退回任务栏左界到开始按钮/托盘留白之间的区域。
    /// </summary>
    public static PixelRect ComputeFallbackRegion(
        PixelRect tray,
        PixelRect? start,
        bool isPrimary)
    {
        var left = tray.Left;
        var rightEdge = start is { } s
            ? s.Left - CardGap
            : isPrimary
                ? tray.Left + tray.Width / 2
                : tray.Right - 170;

        var right = Math.Max(left + MinCardWidth, rightEdge);
        return new PixelRect(left, tray.Top, right, tray.Bottom);
    }

    /// <summary>
    ///     探测任务栏开始按钮矩形。副屏任务栏通常没有开始按钮，返回 null 由调用方回退。
    /// </summary>
    public static PixelRect? DetectStartButtonRect(IntPtr trayHwnd)
    {
        if (trayHwnd == IntPtr.Zero)
            return null;

        var startHwnd = NativeMethods.FindWindowEx(trayHwnd, IntPtr.Zero, "Start", null);
        if (startHwnd == IntPtr.Zero)
            return null;

        if (!NativeMethods.GetWindowRect(startHwnd, out var rect) ||
            rect.Width <= 0 ||
            rect.Height <= 0)
            return null;

        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}
