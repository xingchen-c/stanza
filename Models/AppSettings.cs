namespace FluentLyrics.Models;

/// <summary>应用设置（JSON 持久化）</summary>
public sealed class AppSettings
{
    /// <summary>歌词字号（DIP）</summary>
    public double FontSize { get; set; } = 15;

    /// <summary>歌词颜色（ARGB 十六进制，如 #FFFFFFFF）</summary>
    public string LyricColor { get; set; } = "#FFFFFFFF";

    /// <summary>跑马灯速度（DIP/秒）</summary>
    public double MarqueeSpeed { get; set; } = 55;

    /// <summary>歌词全局时间偏移（毫秒，正数 = 歌词提前显示）</summary>
    public int LyricOffsetMs { get; set; }

    /// <summary>鼠标点击穿透</summary>
    public bool ClickThrough { get; set; } = true;

    /// <summary>开机自启</summary>
    public bool AutoStart { get; set; }
}
