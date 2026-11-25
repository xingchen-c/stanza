using Stanza.Core.Models;

namespace Stanza.Core.Lyrics;

/// <summary>歌词来源：一个实现对应一个搜索/解析源（QQ 音乐、网易云、LRCLIB、本地 LRC…）</summary>
public interface ILyricsProvider
{
    /// <summary>来源唯一键（如 "QQ" / "LRCLIB"），用于候选选择记忆</summary>
    string Key { get; }

    /// <summary>来源名称（日志/调试用）</summary>
    string Name { get; }

    /// <summary>按统一查询加载歌词；返回结果（含行列表）</summary>
    Task<LyricsResult> LoadAsync(LyricsQuery query);

    /// <summary>返回搜索元数据，供自动与手动统一排序；不支持的源返回空列表</summary>
    Task<IReadOnlyList<LyricCandidate>> GetCandidatesAsync(LyricsQuery query)
    {
        return Task.FromResult<IReadOnlyList<LyricCandidate>>([]);
    }

    /// <summary>按已选候选直接取歌词（自动与手动共用）；不支持的源返回 NotFound</summary>
    Task<LyricsResult> LoadCandidateAsync(LyricCandidate candidate)
    {
        return Task.FromResult(new LyricsResult(LyricsLoadResult.NotFound, []));
    }
}
