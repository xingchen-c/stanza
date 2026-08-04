namespace FluentLyrics.Models;

/// <summary>物理像素矩形（屏幕坐标），供任务栏组件定位器返回</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
