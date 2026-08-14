namespace Stanza.Models;

/// <summary>歌词加载结果</summary>
public enum LyricsLoadResult
{
    /// <summary>有同步歌词行，正常显示</summary>
    Loaded,

    /// <summary>歌曲存在但没有时间轴歌词（纯音乐等），显示占位文案</summary>
    Instrumental,

    /// <summary>没搜到歌曲或获取失败</summary>
    NotFound
}