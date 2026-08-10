using System.Windows;
using FluentLyrics.Models;

namespace FluentLyrics;

/// <summary>歌词候选选择窗口：列出可选的歌词版本，用户挑一个应用</summary>
public partial class LyricsPickerWindow : Window
{
    private readonly IReadOnlyList<LyricCandidate> _candidates;

    /// <summary>用户选中的候选（DialogResult=true 时有效）</summary>
    public LyricCandidate? SelectedCandidate { get; private set; }

    public LyricsPickerWindow(IReadOnlyList<LyricCandidate> candidates)
    {
        InitializeComponent();
        _candidates = candidates;

        if (candidates.Count == 0)
        {
            StatusText.Text = "没有可选的歌词候选（当前歌曲可能搜不到多个版本）。";
            ApplyButton.IsEnabled = false;
            return;
        }

        var views = candidates.Select(ToView).ToList();
        CandidateList.ItemsSource = views;
        CandidateList.SelectedIndex = 0;
    }

    /// <summary>匹配度转中文</summary>
    private static string FormatMatchType(string? matchType)
    {
        return matchType?.ToLowerInvariant() switch
        {
            "perfect" => "完美匹配",
            "prettyhigh" => "匹配度高",
            "high" => "匹配度高",
            "medium" => "匹配度中",
            "low" => "匹配度低",
            "unknown" => "匹配度未知",
            null or "" => "匹配度未知",
            _ => $"匹配度 {matchType}"
        };
    }

    /// <summary>候选展示：主行 歌名-歌手，副行 专辑 · 时长 · 匹配度</summary>
    private static CandidateView ToView(LyricCandidate c)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Album))
            parts.Add(c.Album);
        if (c.DurationMs is { } ms)
            parts.Add($"{ms / 60000}:{ms % 60000 / 1000:00}");
        parts.Add(FormatMatchType(c.MatchType));

        return new CandidateView(
            $"{c.Title} - {c.Artist}",
            string.Join(" · ", parts),
            c);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedIndex < 0 || CandidateList.SelectedIndex >= _candidates.Count)
            return;

        SelectedCandidate = ((CandidateView)CandidateList.SelectedItem).Candidate;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>列表展示用的视图模型</summary>
    private sealed record CandidateView(string Title, string Detail, LyricCandidate Candidate);
}
