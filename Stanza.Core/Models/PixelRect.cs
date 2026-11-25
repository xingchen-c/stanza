namespace Stanza.Core.Models;

/// <summary>物理像素矩形（屏幕坐标），供任务栏组件定位器返回</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>矩形宽度（物理像素）。</summary>
    public int Width => Right - Left;

    /// <summary>矩形高度（物理像素）。</summary>
    public int Height => Bottom - Top;
}
