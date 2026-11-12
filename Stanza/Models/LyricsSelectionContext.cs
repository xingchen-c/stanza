namespace Stanza.Models;

/// <summary>打开歌词选择时捕获的曲目身份；同名歌曲再次播放也属于新的修订。</summary>
public sealed record LyricsSelectionContext(LyricsQuery Query, long Revision)
{
    public string Key => Query.Key;
}
