using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Providers;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The anime search finds TV and streaming (ONA) series; anime from the excluded countries only when asked to.
/// </summary>
public sealed class AniListClientTests
{
    private const string Response = """
        {"data":{"Page":{"media":[
          {"id":1,"title":{"english":"A TV Show","romaji":"Terebi","native":null},"format":"TV","status":"FINISHED","countryOfOrigin":"JP","seasonYear":2024,"relations":{"edges":[]}},
          {"id":206914,"title":{"english":"NIPPON SANGOKU: The Three Nations of the Crimson Sun","romaji":"Nippon Sangoku","native":null},"format":"ONA","status":"RELEASING","countryOfOrigin":"JP","seasonYear":2026,"relations":{"edges":[]}},
          {"id":156092,"title":{"english":"To Be Hero X","romaji":"Tu Bian Yingxiong X","native":null},"format":"ONA","status":"FINISHED","countryOfOrigin":"CN","seasonYear":2025,"relations":{"edges":[]}},
          {"id":3,"title":{"english":"A Movie","romaji":"Eiga","native":null},"format":"MOVIE","status":"FINISHED","countryOfOrigin":"JP","seasonYear":2024,"relations":{"edges":[]}}
        ]}}}
        """;

    private static AniListClient CreateClient()
    {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new StubHandler()));
        var options = new InternetProviderOptions { AniListGraphQlUrl = "https://graphql.anilist.test" };
        return new AniListClient(http.Object, Mock.Of<IOptionsMonitor<InternetProviderOptions>>(o => o.CurrentValue == options), NullLogger<AniListClient>.Instance);
    }

    [Fact]
    public async Task Search_FindsTvAndOnaSeries_NotExcludedCountries()
    {
        var results = await CreateClient().SearchSeriesAsync("anything", CancellationToken.None);

        Assert.Equal(["A TV Show", "NIPPON SANGOKU: The Three Nations of the Crimson Sun"], results.Select(r => r.Title));
    }

    [Fact]
    public async Task Search_AllCountries_FindsExcludedCountriesToo()
    {
        var results = await CreateClient().SearchSeriesAsync("anything", CancellationToken.None, allCountries: true);

        Assert.Contains(results, r => r.Title == "To Be Hero X");
        Assert.DoesNotContain(results, r => r.Title == "A Movie");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response, Encoding.UTF8, "application/json") });
    }
}
