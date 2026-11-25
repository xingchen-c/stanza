using System.Windows;
using System.Windows.Controls;
using Stanza.Models;

namespace Stanza;

/// <summary>歌词候选选择窗口：查询和应用始终绑定打开窗口时的曲目。</summary>
public partial class LyricsPickerWindow : Window
{
    private readonly LyricsSelectionContext _context;
    private readonly Func<LyricsSelectionContext, CancellationToken, Task<IReadOnlyList<LyricCandidate>>> _search;
    private readonly Func<LyricsSelectionContext, LyricCandidate, CancellationToken, Task<bool>> _apply;
    private readonly Func<LyricsSelectionContext, bool> _isCurrent;
    private readonly CancellationTokenSource _cancellation = new();
    private IReadOnlyList<LyricCandidate> _candidates = [];
    private bool _busy;
    private bool _closed;
    private bool _stale;
    private bool _loaded;

    public LyricsPickerWindow(
        LyricsSelectionContext context,
        Func<LyricsSelectionContext, CancellationToken, Task<IReadOnlyList<LyricCandidate>>> search,
        Func<LyricsSelectionContext, LyricCandidate, CancellationToken, Task<bool>> apply,
        Func<LyricsSelectionContext, bool> isCurrent)
    {
        InitializeComponent();
        _context = context;
        _search = search;
        _apply = apply;
        _isCurrent = isCurrent;
        TrackText.Text = $"{context.Query.Title} - {context.Query.Artist}";
        Loaded += async (_, _) => await LoadCandidatesAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _cancellation.Cancel();
            _cancellation.Dispose();
        };
    }

    /// <summary>由主窗口已有轮询检查；失效的列表只能关闭后重新打开。</summary>
    internal void RefreshTrackContext()
    {
        if (_closed || _stale || _isCurrent(_context))
            return;

        _stale = true;
        _cancellation.Cancel();
        StatusText.Text = "曲目已变化，请关闭此窗口后重新打开歌词选择。";
        UpdateControls();
    }

    private async Task LoadCandidatesAsync()
    {
        RefreshTrackContext();
        if (_busy || _closed || _stale)
            return;

        _busy = true;
        StatusText.Text = "正在搜索歌词版本…";
        UpdateControls();
        try
        {
            var candidates = await _search(_context, _cancellation.Token);
            RefreshTrackContext();
            if (_closed || _stale)
                return;

            _candidates = candidates;
            _loaded = true;
            RefreshCandidates();
        }
        catch (OperationCanceledException) when (_closed || _stale || _cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_closed && !_stale)
                StatusText.Text = "搜索失败，请点击“重新搜索”重试。";
        }
        finally
        {
            _busy = false;
            if (!_closed)
                UpdateControls();
        }
    }

    private void RefreshCandidates()
    {
        if (_stale || !_loaded)
            return;

        var previous = (CandidateList.SelectedItem as CandidateView)?.Candidate;
        var candidates = FilterCandidates(_candidates, ShowOtherArtistsCheckBox.IsChecked == true);
        var views = candidates.Select(ToView).ToList();
        CandidateList.ItemsSource = views;
        CandidateList.SelectedItem = views.FirstOrDefault(view => view.Candidate == previous);
        if (CandidateList.SelectedIndex < 0 && views.Count > 0)
            CandidateList.SelectedIndex = 0;

        StatusText.Text = candidates.Count == 0
            ? _candidates.Count == 0
                ? "暂未获取到候选，可重新搜索。"
                : "未找到歌名匹配的歌词版本，已过滤其他歌曲，可重新搜索。"
            : $"找到 {candidates.Count} 个歌词版本，选择后点击应用。";
        UpdateControls();
    }

    /// <summary>歌名过滤始终生效；勾选时只放宽歌手限制，无同歌手结果时保留别名纠错入口。</summary>
    internal static IReadOnlyList<LyricCandidate> FilterCandidates(
        IReadOnlyList<LyricCandidate> candidates, bool showOtherArtists)
    {
        var related = candidates.Where(candidate => candidate.TitleMatched != false).ToList();
        return !showOtherArtists && related.Any(candidate => candidate.ArtistMatched == true)
            ? related.Where(candidate => candidate.ArtistMatched != false).ToList()
            : related;
    }

    internal static string FormatMatchType(string? matchType)
    {
        return matchType?.ToLowerInvariant() switch
        {
            "perfect" => "完美匹配",
            "veryhigh" => "匹配度很高",
            "high" => "匹配度高",
            "prettyhigh" => "匹配度较高",
            "medium" => "匹配度中",
            "low" => "匹配度低",
            "verylow" => "匹配度很低",
            "nomatch" => "不匹配",
            "unknown" or null or "" => "匹配度未知",
            _ => $"匹配度 {matchType}"
        };
    }

    private static CandidateView ToView(LyricCandidate candidate)
    {
        var parts = new List<string> { candidate.SourceName };
        if (candidate.PreferredSource)
            parts.Add("与当前播放器同源");
        if (candidate.VersionMatched == false)
            parts.Add("版本标记不同");
        if (candidate.TitleMatched == false)
            parts.Add("歌名不一致");
        if (candidate.ArtistMatched == false)
            parts.Add("歌手可能不一致");
        parts.Add(FormatMatchType(candidate.MatchType));
        if (candidate.MatchScore > 0)
            parts.Add($"匹配等级分 {candidate.MatchScore * 100:F0}");
        if (candidate.DurationMs is { } ms)
            parts.Add($"{ms / 60000}:{ms % 60000 / 1000:00}");
        if (!string.IsNullOrWhiteSpace(candidate.Album))
            parts.Add(candidate.Album);
        return new CandidateView($"{candidate.Title} - {candidate.Artist}", string.Join(" · ", parts), candidate);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        RefreshTrackContext();
        if (_busy || _closed || _stale || CandidateList.SelectedItem is not CandidateView view)
            return;

        _busy = true;
        StatusText.Text = "正在应用所选歌词…";
        UpdateControls();
        try
        {
            var applied = await _apply(_context, view.Candidate, _cancellation.Token);
            RefreshTrackContext();
            if (_closed || _stale)
                return;

            if (applied)
                DialogResult = true;
            else
                StatusText.Text = "所选版本未能应用，请换一个版本或重试。";
        }
        catch (OperationCanceledException) when (_closed || _stale || _cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_closed && !_stale)
                StatusText.Text = "应用失败，请重试或选择其他版本。";
        }
        finally
        {
            _busy = false;
            if (!_closed)
                UpdateControls();
        }
    }

    private void UpdateControls()
    {
        ApplyButton.IsEnabled = !_busy && !_stale && CandidateList.SelectedItem is CandidateView;
        RetryButton.IsEnabled = !_busy && !_stale;
        CandidateList.IsEnabled = !_busy && !_stale;
        ShowOtherArtistsCheckBox.IsEnabled = !_busy && !_stale;
        LoadingProgress.Visibility = _busy && !_stale ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadCandidatesAsync();

    private void ShowOtherArtists_Changed(object sender, RoutedEventArgs e) => RefreshCandidates();

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApplyButton is not null)
            UpdateControls();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record CandidateView(string Title, string Detail, LyricCandidate Candidate);
}
