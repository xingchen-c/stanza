using FluentLyrics.Models;

namespace FluentLyrics.Services.Lyrics;

/// <summary>歌词来源：一个实现对应一个搜索/解析源（QQ 音乐、网易云、LRCLIB、本地 LRC…）</summary>
public interface ILyricsProvider
{
    /// <summary>来源名称（日志/调试用）</summary>
    string Name { get; }

    /// <summary>按歌曲信息加载歌词；返回结果（含行列表）</summary>
    Task<LyricsResult> LoadAsync(string title, string artist, int? durationMs);
}