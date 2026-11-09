using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// The single place that decides an item's <see cref="ReleaseStatus"/>. Clients should only ever look at the status.
/// </summary>
public static class ReleaseStatusResolver
{
    /// <summary>
    /// Resolves the release status of an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="utcNow">The current time.</param>
    /// <returns>The status, <see cref="ReleaseStatus.None"/> for regular library items.</returns>
    public static ReleaseStatus Resolve(BaseItem item, DateTime utcNow)
    {
        switch (item)
        {
            case Episode episode:
                return Resolve(
                    episode.IsInternet,
                    episode.IsDownloaded,
                    episode.DownloadStartedUtc.HasValue,
                    episode.FirstAiring,
                    episode.DownloadUrl,
                    episode.SourceCandidates?.Length ?? 0,
                    episode.Scheduled && !episode.SourceCandidatesUpdatedUtc.HasValue,
                    utcNow);
            case Movie movie:
                return Resolve(
                    movie.IsInternet,
                    movie.IsDownloaded,
                    movie.DownloadStartedUtc.HasValue,
                    null,
                    movie.DownloadUrl,
                    0,
                    movie.Scheduled,
                    utcNow);
            default:
                return ReleaseStatus.None;
        }
    }

    private static ReleaseStatus Resolve(
        bool isInternet,
        bool isDownloaded,
        bool isDownloading,
        DateTime? firstAiring,
        string? downloadUrl,
        int candidateCount,
        bool searching,
        DateTime utcNow)
    {
        var hasUrl = !string.IsNullOrEmpty(downloadUrl);

        // Finished downloads have IsInternet cleared, so they're recognised by the url they were downloaded from.
        if (!isInternet)
        {
            return hasUrl && isDownloaded ? ReleaseStatus.Downloaded : ReleaseStatus.None;
        }

        if (isDownloaded)
        {
            return ReleaseStatus.Downloaded;
        }

        if (isDownloading)
        {
            return ReleaseStatus.Downloading;
        }

        if (firstAiring.HasValue && ToUtc(firstAiring.Value) > utcNow)
        {
            return ReleaseStatus.Unaired;
        }

        if (hasUrl)
        {
            return ReleaseStatus.Available;
        }

        if (candidateCount > 0)
        {
            return ReleaseStatus.ChooseSource;
        }

        // An episode whose fallback search came up empty is NotFound (so it can be requested from an admin), even
        // though the authors keep being retried in the background until the search gives up.
        return searching ? ReleaseStatus.Searching : ReleaseStatus.NotFound;
    }

    /// <summary>
    /// Treats unspecified kinds as UTC, which is how PirateFin stores every timestamp.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The value as UTC.</returns>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
