using System;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

public class ReleaseStatusResolverTests
{
    private static readonly DateTime _now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static Episode Placeholder(DateTime? firstAiring) => new()
    {
        IsInternet = true,
        Scheduled = true,
        IsDownloaded = false,
        IsVirtualItem = true,
        FirstAiring = firstAiring
    };

    [Fact]
    public void RegularEpisode_HasNoStatus()
        => Assert.Equal(ReleaseStatus.None, ReleaseStatusResolver.Resolve(new Episode(), _now));

    [Fact]
    public void FutureEpisode_IsUnaired()
        => Assert.Equal(ReleaseStatus.Unaired, ReleaseStatusResolver.Resolve(Placeholder(_now.AddMinutes(1)), _now));

    [Fact]
    public void AiredEpisodeWithoutSource_IsSearching_NotMissing()
    {
        var episode = Placeholder(_now.AddDays(-1));

        Assert.Equal(ReleaseStatus.Searching, ReleaseStatusResolver.Resolve(episode, _now));
        Assert.False(episode.IsMissingEpisode);
    }

    [Fact]
    public void FallbackCandidates_AreChooseSource()
    {
        var episode = Placeholder(_now.AddDays(-1));
        episode.SourceCandidates = [new TorrentCandidate { Title = "x", MagnetUrl = "magnet:?xt=urn:btih:abc" }];

        Assert.Equal(ReleaseStatus.ChooseSource, ReleaseStatusResolver.Resolve(episode, _now));
    }

    [Fact]
    public void KnownSource_IsAvailable_ThenDownloading_ThenDownloaded()
    {
        var episode = Placeholder(_now.AddDays(-1));
        episode.DownloadUrl = "magnet:?xt=urn:btih:abc";
        Assert.Equal(ReleaseStatus.Available, ReleaseStatusResolver.Resolve(episode, _now));

        episode.DownloadStartedUtc = _now;
        Assert.Equal(ReleaseStatus.Downloading, ReleaseStatusResolver.Resolve(episode, _now));

        // What DownloadManager does on completion.
        episode.IsDownloaded = true;
        episode.IsInternet = false;
        episode.DownloadStartedUtc = null;
        Assert.Equal(ReleaseStatus.Downloaded, ReleaseStatusResolver.Resolve(episode, _now));
    }

    [Fact]
    public void GivenUpWithoutCandidates_IsNotFound()
    {
        var episode = Placeholder(_now.AddDays(-30));
        episode.Scheduled = false;

        Assert.Equal(ReleaseStatus.NotFound, ReleaseStatusResolver.Resolve(episode, _now));
    }

    [Fact]
    public void EmptyFallbackSearch_IsNotFound_WhileAuthorsAreStillRetried()
    {
        var episode = Placeholder(_now.AddDays(-1));
        episode.SourceCandidatesUpdatedUtc = _now.AddMinutes(-5);

        Assert.Equal(ReleaseStatus.NotFound, ReleaseStatusResolver.Resolve(episode, _now));
    }

    [Fact]
    public void UnspecifiedKind_IsTreatedAsUtc()
    {
        var episode = Placeholder(DateTime.SpecifyKind(_now.AddMinutes(30), DateTimeKind.Unspecified));

        Assert.Equal(ReleaseStatus.Unaired, ReleaseStatusResolver.Resolve(episode, _now));
    }

    [Fact]
    public void MovieWithMagnet_IsAvailable()
    {
        var movie = new Movie { IsInternet = true, IsDownloaded = false, DownloadUrl = "magnet:?xt=urn:btih:abc" };

        Assert.Equal(ReleaseStatus.Available, ReleaseStatusResolver.Resolve(movie, _now));
    }
}
