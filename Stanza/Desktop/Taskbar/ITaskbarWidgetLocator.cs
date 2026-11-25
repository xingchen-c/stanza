using Stanza.Core.Models;

namespace Stanza.Desktop.Taskbar;

/// <summary>任务栏组件定位器：返回卡片应紧贴其右侧的组件矩形；找不到返回 null</summary>
public interface ITaskbarWidgetLocator
{
    /// <summary>定位器唯一键（设置白名单用，如 "FluentFlyout" / "Independent"）</summary>
    string Key { get; }

    /// <summary>定位器名称（日志/调试用）</summary>
    string Name { get; }

    /// <summary>探测组件矩形（屏幕物理像素）；失败返回 null</summary>
    PixelRect? Detect(IntPtr trayHwnd, PixelRect trayRect);

    /// <summary>
    ///     推荐卡片区域（屏幕物理像素）；锚点定位器无需实现（默认返回 null）。
    ///     独立定位器不依赖任何外部组件，直接给出卡片应占用的区域。
    /// </summary>
    PixelRect? DetectCardRegion(IntPtr trayHwnd, PixelRect trayRect)
    {
        return null;
    }
}