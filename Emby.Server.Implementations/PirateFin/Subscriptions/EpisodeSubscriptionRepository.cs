using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// Library backed <see cref="IEpisodeSubscriptionRepository"/>.
/// </summary>
public sealed class EpisodeSubscriptionRepository : IEpisodeSubscriptionRepository
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeSubscriptionRepository"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    public EpisodeSubscriptionRepository(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<Episode> GetDueEpisodes(DateTime utcNow, int limit, Func<Episode, bool> skip)
    {
        // IsInternet / Scheduled are indexed columns; the rest of the state lives in the item data.
        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                Recursive = true,
                IsInternet = true,
                Scheduled = true
            })
            .OfType<Episode>()
            .Where(e => !e.IsDownloaded
                && !e.DownloadStartedUtc.HasValue
                && string.IsNullOrEmpty(e.DownloadUrl)
                && e.FirstAiring.HasValue
                && ReleaseStatusResolver.ToUtc(e.FirstAiring.Value) <= utcNow
                && (!e.NextSourceSearchUtc.HasValue || e.NextSourceSearchUtc.Value <= utcNow)
                && !skip(e))
            .OrderBy(e => e.NextSourceSearchUtc ?? DateTime.MinValue)
            .ThenByDescending(e => e.FirstAiring)
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc />
    public bool IsSeasonSubscribed(Episode episode) => episode.Season?.Subscribed == true;

    /// <inheritdoc />
    public IReadOnlyList<Season> GetSubscribedSeasons()
        => _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Season], Recursive = true })
            .OfType<Season>()
            .Where(s => s.Subscribed)
            .ToList();

    /// <inheritdoc />
    public Task SaveAsync(Episode episode, CancellationToken cancellationToken)
        => episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);
}
