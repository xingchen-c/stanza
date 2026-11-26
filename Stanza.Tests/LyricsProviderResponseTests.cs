using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lyricify.Lyrics.Providers.Web;
using Stanza.Core.Models;
using Stanza.Core.Lyrics;
using Xunit;

namespace Stanza.Tests;

[CollectionDefinition("LyricsProviderHttp", DisableParallelization = true)]
public sealed class LyricsProviderHttpCollection;

[Collection("LyricsProviderHttp")]
public class LyricsProviderResponseTests
{
    [Theory]
    [InlineData("三春序", "浮生梦/汐音社", "346389934", 226667, 226)]
    [InlineData("朝花·烛悴", "乐正绫", "105979722", 258417, 258)]
    public async Task QQ组合搜索为空时仍按标题召回并以原始歌手打分(
        string title, string artist, string id, int durationMs, int seconds)
    {
        var searches = new List<string>();
        using var client = new HttpClient(new ResponseHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var keyword = document.RootElement.GetProperty("req_1").GetProperty("param")
                .GetProperty("query").GetString()!;
            searches.Add(keyword);
            var songs = keyword == title
                ? new[]
                {
                    new { id, title, interval = seconds, album = new { title },
                        singer = artist.Split('/').Select(name => new { name }).ToArray() },
                    new { id = "other", title, interval = seconds, album = new { title },
                        singer = new[] { new { name = "其他歌手" } } }
                }
                : [];
            return JsonSerializer.Serialize(new
            {
                code = 0,
                req_1 = new { code = 0, data = new { body = new { song = new { list = songs } } } }
            });
        }));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var query = new LyricsQuery(title, artist, durationMs, null, "QQ");
            var candidates = await new QqMusicLyricsProvider().GetCandidatesAsync(query);
            Assert.Equal(new[] { $"{title} {artist}", title }, searches);
            var ranked = LyricsCandidateRanker.Rank(candidates, query);
            Assert.Equal(id, ranked[0].Id);
            Assert.True(ranked[0].ArtistMatched);
            Assert.False(ranked[1].ArtistMatched);
            Assert.Equal(id, Assert.Single(LyricsCandidateRanker.Decide(ranked, query).Candidates).Id);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task QQ桌面搜索为空或报2001时通过Mobile召回并自动取词(int desktopCode)
    {
        var methods = new List<string>();
        using var client = new HttpClient(new ResponseHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("lyric_download"))
                return "<root><content>[00:01.00]Again and again closed my eyes</content></root>";

            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var search = document.RootElement.GetProperty("req_1");
            var method = search.GetProperty("method").GetString()!;
            methods.Add(method);
            if (method == "DoSearchForQQMusicDesktop")
                return JsonSerializer.Serialize(new { code = 0, req_1 = new { code = desktopCode,
                    data = new { code = 0, body = new { song = new { list = Array.Empty<object>() } } } } });

            Assert.Equal("DoSearchForQQMusicMobile", method);
            Assert.Equal("u6.y.qq.com", request.RequestUri.Host);
            Assert.Equal("Rendezvous", search.GetProperty("param").GetProperty("query").GetString());
            Assert.Equal("application/x-www-form-urlencoded", request.Content.Headers.ContentType!.MediaType);
            Assert.Contains("QQMusic 20080508(android)", request.Headers.GetValues("User-Agent"));
            return """
                {"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"item_song":[
                  {"id":"729779783","mid":"002YkHJW1fv4U5","title":"<em>Rendezvous</em>","interval":208,
                   "album":{"title":"<em>Rendezvous</em>"},"singer":[{"name":"<em>Mili</em>"}],
                   "grp":[{"id":"live","mid":"live","title":"Rendezvous (Live)","interval":240,
                           "album":{"title":"Live"},"singer":[{"name":"Mili"}]}]}
                ]}}}}
                """;
        }));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var query = new LyricsQuery("Rendezvous", "Mili (ミリー)", 208533, null, "QQ");
            var chain = new LyricsProviderChain(new QqMusicLyricsProvider());
            var candidates = await chain.GetCandidatesAsync(query);
            Assert.Equal(new[] { "729779783", "live" }, candidates.Select(c => c.Id));
            methods.Clear();
            var result = await chain.LoadAsync(query);
            Assert.Equal(LyricsLoadResult.Loaded, result.Status);
            Assert.Equal("Again and again closed my eyes", Assert.Single(result.Lines).Text);
            Assert.Equal("729779783", result.Decision!.Candidate.Id);
            Assert.Equal("Mili", result.Decision.Candidate.Artist);
            Assert.Equal("Rendezvous", result.Decision.Candidate.Album);
            Assert.Equal(new[] { "DoSearchForQQMusicDesktop", "DoSearchForQQMusicDesktop",
                "DoSearchForQQMusicMobile" }, methods);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 2001, 0)]
    [InlineData(0, 0, 1)]
    public async Task QQMobile接口业务失败时不采用响应中的候选(int code, int moduleCode, int dataCode)
    {
        var mobileRequested = false;
        using var client = new HttpClient(new ResponseHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var method = document.RootElement.GetProperty("req_1").GetProperty("method").GetString();
            if (method == "DoSearchForQQMusicDesktop")
                return """{"code":0,"req_1":{"code":2001}}""";
            mobileRequested = true;
            return JsonSerializer.Serialize(new { code, req_1 = new { code = moduleCode,
                data = new { code = dataCode, body = new { item_song = new[] {
                    new { id = "729779783", mid = "002YkHJW1fv4U5", title = "Rendezvous", interval = 208,
                        album = new { title = "Rendezvous" }, singer = new[] { new { name = "Mili" } } }
                } } } } });
        }));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var candidates = await new QqMusicLyricsProvider().GetCandidatesAsync(
                new LyricsQuery("Rendezvous", "Mili", 208533));
            Assert.True(mobileRequested);
            Assert.Empty(candidates);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Theory]
    [InlineData("其它歌曲", "银临")]
    [InlineData("见闻山海 (剧情版)", "其他歌手")]
    [InlineData("见闻山海 (Live)", "银临")]
    public async Task QQ非空但无相关候选时继续清洗标题并合并去重(string unrelatedTitle, string unrelatedArtist)
    {
        using var client = new HttpClient(new ResponseHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var keyword = document.RootElement.GetProperty("req_1").GetProperty("param")
                .GetProperty("query").GetString();
            var songs = new List<object> { new { id = "other", mid = "other", title = unrelatedTitle,
                interval = 226, album = new { title = "见闻山海" }, singer = new[] { new { name = unrelatedArtist } } } };
            if (keyword == "见闻山海 (剧情版)")
                songs.Add(new { id = "correct", mid = "correct", title = "见闻山海 (剧情版)",
                    interval = 226, album = new { title = "见闻山海" }, singer = new[] { new { name = "银临" } } });
            return JsonSerializer.Serialize(new { code = 0, req_1 = new { code = 0,
                data = new { code = 0, body = new { song = new { list = songs } } } } });
        }));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var query = new LyricsQuery("【盗墓笔记】见闻山海 (剧情版)", "银临", 226000);
            var candidates = await new QqMusicLyricsProvider().GetCandidatesAsync(query);
            Assert.Equal(2, candidates.Count);
            Assert.Contains(candidates, c => c.Id == "other");
            Assert.Equal("correct", Assert.Single(LyricsCandidateRanker.Decide(
                LyricsCandidateRanker.Rank(candidates, query), query).Candidates).Id);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Fact]
    public async Task QQ单条候选缺少ID不丢弃同批有效结果()
    {
        using var client = new HttpClient(new ResponseHandler(_ => Task.FromResult("""
            {"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"song":{"list":[
              {"title":"三春序","interval":226,"album":{"title":"三春序"},"singer":[{"name":"浮生梦"}]},
              {"id":"346389934","mid":"valid","title":"三春序","interval":226,
               "album":{"title":"三春序"},"singer":[{"name":"浮生梦"}]}
            ]}}}}}
            """)));
        client.DefaultRequestHeaders.Add("X-Stanza-Test", "keep");
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var candidates = await new QqMusicLyricsProvider().GetCandidatesAsync(
                new LyricsQuery("三春序", "浮生梦", 226000));
            Assert.Equal("346389934", Assert.Single(candidates).Id);
            Assert.Equal("keep", Assert.Single(client.DefaultRequestHeaders.GetValues("X-Stanza-Test")));
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 2001, 0)]
    [InlineData(0, 0, 1)]
    public async Task QQ桌面业务失败不采用其中候选并继续Mobile(int code, int moduleCode, int dataCode)
    {
        using var client = new HttpClient(new ResponseHandler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var mobile = document.RootElement.GetProperty("req_1").GetProperty("method").GetString()
                == "DoSearchForQQMusicMobile";
            var songs = new[] { new { id = mobile ? "correct" : "rejected", mid = "song", title = "三春序",
                interval = 226, album = new { title = "三春序" }, singer = new[] { new { name = "浮生梦" } } } };
            return mobile
                ? JsonSerializer.Serialize(new { code = 0, req_1 = new { code = 0,
                    data = new { code = 0, body = new { item_song = songs } } } })
                : JsonSerializer.Serialize(new { code, req_1 = new { code = moduleCode,
                    data = new { code = dataCode, body = new { song = new { list = songs } } } } });
        }));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var candidates = await new QqMusicLyricsProvider().GetCandidatesAsync(
                new LyricsQuery("三春序", "浮生梦", 226000));
            Assert.Equal("correct", Assert.Single(candidates).Id);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Fact]
    public async Task QQ桌面请求卡住时取消请求并在单源预算内由Mobile召回()
    {
        var desktopCanceled = false;
        using var client = new HttpClient(new CancellableResponseHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (document.RootElement.GetProperty("req_1").GetProperty("method").GetString()
                == "DoSearchForQQMusicDesktop")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { desktopCanceled = true; throw; }
            }
            return """
                {"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"item_song":[
                  {"id":"460094300","mid":"correct","title":"致 (TO.) (Outro)","interval":220,
                   "album":{"title":"24時·Pm"},"singer":[{"name":"Jony J"}]}
                ]}}}}
                """;
        }));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var query = new LyricsQuery("致 (TO.) (Outro)", "Jony J", 220805);
            var candidates = await new LyricsProviderChain(new QqMusicLyricsProvider())
                .GetCandidatesAsync(query).WaitAsync(TimeSpan.FromSeconds(7));
            Assert.True(desktopCanceled);
            Assert.Equal("460094300", Assert.Single(candidates).Id);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Fact]
    public async Task QQ后续搜索超时仍保留先前候选供手选且不放行异歌手()
    {
        var calls = 0;
        var mobileRequested = false;
        using var client = new HttpClient(new CancellableResponseHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            mobileRequested |= document.RootElement.GetProperty("req_1").GetProperty("method").GetString()
                == "DoSearchForQQMusicMobile";
            if (++calls > 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return """
                {"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"song":{"list":[
                  {"id":"other","mid":"other","title":"【前缀】三春序 (Live)","interval":226,
                   "album":{"title":"三春序"},"singer":[{"name":"其他歌手"}]}
                ]}}}}}
                """;
        }));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var query = new LyricsQuery("【前缀】三春序 (Live)", "浮生梦", 226000);
            var candidates = await new LyricsProviderChain(new QqMusicLyricsProvider())
                .GetCandidatesAsync(query).WaitAsync(TimeSpan.FromSeconds(7));
            Assert.True(mobileRequested);
            Assert.Equal("other", Assert.Single(candidates).Id);
            Assert.Equal(LyricsLoadResult.Ambiguous, LyricsCandidateRanker.Decide(candidates, query).Status);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    [Theory]
    [InlineData("{\"code\":200,\"uncollected\":true,\"lrc\":{\"version\":1,\"lyric\":\"\"}}", LyricsLoadResult.NotFound)]
    [InlineData("{\"code\":200}", LyricsLoadResult.NotFound)]
    [InlineData("{\"code\":200,\"lrc\":{\"lyric\":\"无时间轴歌词\"}}", LyricsLoadResult.NotFound)]
    [InlineData("{\"code\":200,\"nolyric\":true}", LyricsLoadResult.Instrumental)]
    [InlineData("{\"code\":200,\"lrc\":{\"lyric\":\"[00:01.00]歌词\"}}", LyricsLoadResult.Loaded)]
    public async Task 网易云只有明确纯音乐标记才返回纯音乐(string response, LyricsLoadResult expected)
    {
        using var client = new HttpClient(new ResponseHandler(_ => Task.FromResult(response)));
        var previous = BaseApi.HttpClient;
        BaseApi.HttpClient = client;
        try
        {
            var candidate = new LyricCandidate("NetEase", "网易云音乐", "2154905641",
                "三春序", "浮生梦, 汐音社", "三春序", 226000, null);
            var result = await new NeteaseLyricsProvider().LoadCandidateAsync(candidate);
            Assert.Equal(expected, result.Status);
            if (expected == LyricsLoadResult.Loaded)
                Assert.Equal("歌词", Assert.Single(result.Lines).Text);
            else
                Assert.Empty(result.Lines);
        }
        finally
        {
            BaseApi.HttpClient = previous;
        }
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(await respond(request), Encoding.UTF8, "application/json")
            };
    }

    private sealed class CancellableResponseHandler(
        Func<HttpRequestMessage, CancellationToken, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(await respond(request, cancellationToken), Encoding.UTF8, "application/json")
            };
    }
}
