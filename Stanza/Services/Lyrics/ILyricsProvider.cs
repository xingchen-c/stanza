using Stanza.Models;

namespace Stanza.Services.Lyrics;

/// <summary>歌词来源：一个实现对应一个搜索/解析源（QQ 音乐、网易云、LRCLIB、本地 LRC…）</summary>
public interface ILyricsProvider
{
    /// <summary>来源唯一键（如 "QQ" / "LRCLIB"），用于候选选择记忆</summary>
    string Key { get; }

    /// <summary>来源名称（日志/调试用）</summary>
    string Name { get; }

    /// <summary>按歌曲信息加载歌词；返回结果（含行列表）</summary>
    Task<LyricsResult> LoadAsync(string title, string artist, int? durationMs);

    /// <summary>搜索候选列表（手动选择歌词用）；不支持的源返回空列表</summary>
    Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(string title, string artist, int? durationMs)
    {
        return Task.FromResult<IReadOnlyList<LyricCandidate>>([]);
    }

    /// <summary>按用户选中的候选直接取歌词；不支持的源返回 NotFound</summary>
    Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        return Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));
    }
}
