using Stanza.Models;
using Stanza.Services;
using Xunit;

namespace Stanza.Tests;

public class MediaRequestTests
{
    [Fact]
    public void 同会话后发元数据请求使旧回包过期()
    {
        var identity = new SmtcSessionService.MediaRequestIdentity();
        var session = new object();
        identity.ChangeSession(session);
        var old = identity.BeginMetadata(session);
        var latest = identity.BeginMetadata(session);
        Assert.False(identity.IsCurrent(old));
        Assert.True(identity.IsCurrent(latest));
    }

    [Fact]
    public void A到B再到A不能复用旧选择上下文()
    {
        var identity = new SmtcSessionService.MediaRequestIdentity();
        identity.ChangeSession(new object());
        identity.ConfirmTrack(new("A", "歌手", null));
        var old = Assert.IsType<LyricsSelectionContext>(identity.CreateContext());
        identity.ConfirmTrack(new("B", "歌手", null));
        identity.ConfirmTrack(new("A", "歌手", null));
        Assert.False(identity.IsSelectionCurrent(old));
        Assert.True(identity.IsSelectionCurrent(identity.CreateContext()!));
    }

    [Fact]
    public void 会话替换断开及空标题不接受旧上下文或旧请求()
    {
        var identity = new SmtcSessionService.MediaRequestIdentity();
        var session = new object();
        identity.ChangeSession(session);
        identity.ConfirmTrack(new("A", "歌手", null));
        var context = identity.CreateContext()!;
        var request = identity.BeginMetadata(session);
        identity.ChangeSession(new object());
        Assert.False(identity.IsCurrent(request));
        Assert.False(identity.IsSelectionCurrent(context));
        identity.ConfirmTrack(new("A", "歌手", null));
        identity.SetMetadataUnavailable();
        Assert.Null(identity.CreateContext());
        identity.ChangeSession(null);
        Assert.Null(identity.CreateContext());
    }

    [Fact]
    public void 同歌元数据更新不作废手选上下文但切歌会改变revision()
    {
        var identity = new SmtcSessionService.MediaRequestIdentity();
        identity.ChangeSession(new object());
        identity.ConfirmTrack(new("A", "歌手", null));
        var context = identity.CreateContext()!;
        identity.ConfirmTrack(new("A", "歌手", 200000));
        Assert.True(identity.IsSelectionCurrent(context));
        identity.ConfirmTrack(new("B", "歌手", null));
        Assert.False(identity.IsSelectionCurrent(context));
    }
}
