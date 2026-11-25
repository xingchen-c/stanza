namespace Stanza.Core.Models;

/// <summary>歌词源返回的结果：状态 + 行列表</summary>
public sealed record LyricsResult(LyricsLoadResult Status, IReadOnlyList<LyricLine> Lines)
{
    public LyricsDecisionEvidence? Decision { get; init; }
}

/// <summary>自动决策的可追溯依据；匹配算法升级或查询变化后不复用旧决策。</summary>
public sealed record LyricsDecisionEvidence(int AlgorithmVersion, string QueryFingerprint, LyricCandidate Candidate);
