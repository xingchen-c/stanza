namespace Stanza.Composition;

/// <summary>按需创建窗口并绑定应用契约；窗口由关闭流程清理，避免被根容器长期持有。</summary>
internal sealed class WindowFactory(ICommandBus commands, INowPlayingQueries queries, ISettingsStore settings, IAutoStart autoStart)
{
    public MainWindow CreateMainWindow() => new(commands, queries, this, settings);
    public SettingsWindow CreateSettingsWindow(AppSettings current) => new(current, settings, autoStart);
    public LyricsPickerWindow CreateLyricsPicker(LyricsSelectionContext context) => new(
        context, queries.GetLyricCandidatesAsync,
        (selection, candidate, token) => commands.SendAsync(new ApplyLyricsCandidate(selection, candidate), token),
        queries.IsLyricsSelectionCurrent, queries.GetLyricOffset(context),
        (selection, offset, token) => commands.SendAsync(new SetLyricOffset(selection, offset), token),
        (selection, token) => commands.SendAsync(new RestoreAutomaticLyrics(selection), token));
}
