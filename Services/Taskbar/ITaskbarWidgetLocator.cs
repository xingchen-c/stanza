using FluentLyrics.Models;

namespace FluentLyrics.Services.Taskbar;

/// <summary>任务栏组件定位器：返回卡片应紧贴其右侧的组件矩形；找不到返回 null</summary>
public interface ITaskbarWidgetLocator
{
    /// <summary>定位器名称（日志/调试用）</summary>
    string Name { get; }

    /// <summary>探测组件矩形（屏幕物理像素）；失败返回 null</summary>
    PixelRect? Detect(IntPtr trayHwnd, PixelRect trayRect);
}
