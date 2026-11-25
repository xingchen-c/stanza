using Stanza.Core.Models;

namespace Stanza.Core.Lyrics;

internal interface ILyricsChoiceStore
{
    void Initialize();
    bool TryGet(string key, out LyricCandidate? candidate);
    void Put(string key, LyricCandidate candidate);
    void Remove(string key);
    void Clear();
}
