using Stanza.Models;
using Stanza.Services.Desktop;
using Xunit;

namespace Stanza.Tests;

/// <summary>只验证原生样式决策和候选展示纯逻辑，不创建窗口或访问播放器。</summary>
public class MediaInteractionTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    public void ExtendedStyle_仅非交互区且启用穿透时设置透明样式(
        bool adjusting, bool clickThrough, bool mediaHit, bool transparent)
    {
        const long unrelatedFlags = 0x00080080;
        var result = WindowInteropService.CalculateExtendedStyle(unrelatedFlags, adjusting, clickThrough, mediaHit);
        Assert.Equal(transparent, (result & 0x20) != 0);
        Assert.Equal(unrelatedFlags, result & unrelatedFlags);
        Assert.NotEqual(0, result & 0x08000000);
        Assert.Equal(result, WindowInteropService.CalculateExtendedStyle(result, adjusting, clickThrough, mediaHit));
    }

    [Fact]
    public void ExtendedStyle_离开媒体区恢复穿透再次进入即清除()
    {
        var outside = WindowInteropService.CalculateExtendedStyle(0, false, true, false);
        var inside = WindowInteropService.CalculateExtendedStyle(outside, false, true, true);
        var outsideAgain = WindowInteropService.CalculateExtendedStyle(inside, false, true, false);
        Assert.Equal(0, inside & 0x20);
        Assert.Equal(outside, outsideAgain);
    }

    [Fact]
    public void Filter_默认收起异歌以及存在同歌手时的异歌手同名()
    {
        var expected = Candidate("理想不大", true, true);
        var wrongTitle = Candidate("回旋", false, true);
        var wrongArtist = Candidate("理想不大", true, false);
        var unknownArtist = Candidate("理想不大 (Live)", true, null);
        LyricCandidate[] all = [expected, wrongTitle, wrongArtist, unknownArtist];

        Assert.Equal([expected, unknownArtist], LyricsPickerWindow.FilterCandidates(all, false));
        Assert.Same(all, LyricsPickerWindow.FilterCandidates(all, true));
    }

    [Fact]
    public void Filter_不因异歌手中的同歌手标记误收起歌名相关结果()
    {
        var wrongTitle = Candidate("回旋", false, true);
        var alias = Candidate("理想不大", true, false);
        Assert.Equal([alias], LyricsPickerWindow.FilterCandidates([wrongTitle, alias], false));
    }

    [Fact]
    public void Filter_无同歌手时保留别名和旧源未知字段()
    {
        var alias = Candidate("理想不大", true, false);
        var legacy = Candidate("旧源结果", null, null);
        Assert.Equal([alias, legacy], LyricsPickerWindow.FilterCandidates([alias, legacy], false));
    }

    [Fact]
    public void Filter_没有相关结果不会自动显示异歌()
    {
        var wrongTitle = Candidate("回旋", false, true);
        Assert.Empty(LyricsPickerWindow.FilterCandidates([wrongTitle], false));
        Assert.Equal([wrongTitle], LyricsPickerWindow.FilterCandidates([wrongTitle], true));
    }

    [Theory]
    [InlineData("Perfect", "完美匹配")]
    [InlineData("PrettyHigh", "匹配度很高")]
    [InlineData("VeryHigh", "匹配度很高")]
    [InlineData("VERYHIGH", "匹配度很高")]
    [InlineData("VeryLow", "匹配度很低")]
    [InlineData("NoMatch", "不匹配")]
    [InlineData(null, "匹配度未知")]
    public void MatchType_显示中文档位(string? matchType, string expected)
    {
        Assert.Equal(expected, LyricsPickerWindow.FormatMatchType(matchType));
    }

    private static LyricCandidate Candidate(string title, bool? titleMatched, bool? artistMatched) =>
        new("QQ", "QQ 音乐", title, title, "歌手", null, null, "VeryHigh")
        {
            TitleMatched = titleMatched,
            ArtistMatched = artistMatched
        };
}
