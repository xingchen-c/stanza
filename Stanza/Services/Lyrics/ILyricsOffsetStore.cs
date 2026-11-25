namespace Stanza.Services.Lyrics;

internal interface ILyricsOffsetStore
{
    const int MaximumOffsetMs = 10_000;
    void Initialize();
    int Get(string key);
    bool Set(string key, int offsetMs);
}
