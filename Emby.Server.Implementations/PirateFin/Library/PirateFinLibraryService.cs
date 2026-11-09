using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Fills the PirateFin libraries from the weekly schedule and resets them.
/// </summary>
public sealed class PirateFinLibraryService : IPirateFinLibraryService
{
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly IInternetScheduleFetcher _scheduleFetcher;
    private readonly IEpisodeGenerator _episodeGenerator;
    private readonly IMovieGenerator _movieGenerator;
    private readonly ILogger<PirateFinLibraryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinLibraryService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="scheduleFetcher">The schedule fetcher.</param>
    /// <param name="episodeGenerator">The episode generator.</param>
    /// <param name="movieGenerator">The movie generator.</param>
    /// <param name="logger">The logger.</param>
    public PirateFinLibraryService(
        ILibraryManager libraryManager,
        IInternetScheduleFetcher scheduleFetcher,
        IEpisodeGenerator episodeGenerator,
        IMovieGenerator movieGenerator,
        ILogger<PirateFinLibraryService> logger)
    {
        _libraryManager = libraryManager;
        _scheduleFetcher = scheduleFetcher;
        _episodeGenerator = episodeGenerator;
        _movieGenerator = movieGenerator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string library, CancellationToken cancellationToken)
    {
        switch (PirateFinLibraries.Normalize(library))
        {
            case PirateFinLibraries.Anime:
                var anime = await _scheduleFetcher.GetAnimeWeeklyScheduleAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Fetched {Count} anime episodes from AniList", anime.Count);
                await _episodeGenerator.GenerateEpisodesAsync(anime, PirateFinLibraries.Anime, cancellationToken).ConfigureAwait(false);
                break;
            case PirateFinLibraries.Shows:
                var shows = await _scheduleFetcher.GetTvWeeklyScheduleAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Fetched {Count} TV episodes from Trakt", shows.Count);
                await _episodeGenerator.GenerateEpisodesAsync(shows, PirateFinLibraries.Shows, cancellationToken).ConfigureAwait(false);
                break;
            case PirateFinLibraries.Movies:
                var movies = await _scheduleFetcher.GetLatestMoviesAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Fetched {Count} movies from YTS", movies.Count);
                await _movieGenerator.GenerateMoviesAsync(movies, PirateFinLibraries.Movies, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Unknown PirateFin library '{library}'.", nameof(library));
        }
    }

    /// <inheritdoc />
    public PlaceholderRemovalResult RemovePlaceholders(string library)
    {
        var name = PirateFinLibraries.Normalize(library) ?? throw new ArgumentException($"Unknown PirateFin library '{library}'.", nameof(library));
        var result = new PlaceholderRemovalResult();
        var target = PirateFinLibrary.Resolve(_libraryManager, name, _logger);
        if (target is null || target.CollectionFolderId.IsEmpty())
        {
            return result;
        }

        if (name == PirateFinLibraries.Movies)
        {
            foreach (var movie in Query<Movie>(target, BaseItemKind.Movie, isInternet: true).Where(IsRemovable))
            {
                result.Movies += Delete(movie) ? 1 : 0;
            }

            return result;
        }

        foreach (var episode in Query<Episode>(target, BaseItemKind.Episode, isInternet: true).Where(IsRemovable))
        {
            result.Episodes += Delete(episode) ? 1 : 0;
        }

        // Seasons / series created by PirateFin stay IsInternet; they go once nothing (downloaded or downloading) is left.
        foreach (var season in Query<Season>(target, BaseItemKind.Season, isInternet: true).Where(IsEmpty))
        {
            result.Seasons += DeleteWithEmptyFolder(season) ? 1 : 0;
        }

        foreach (var series in Query<Series>(target, BaseItemKind.Series, isInternet: true).Where(IsEmpty))
        {
            result.Series += DeleteWithEmptyFolder(series) ? 1 : 0;
        }

        return result;
    }

    /// <inheritdoc />
    public PlaceholderRemovalResult? StartReset(string library)
    {
        var name = PirateFinLibraries.Normalize(library) ?? throw new ArgumentException($"Unknown PirateFin library '{library}'.", nameof(library));
        if (!_running.TryAdd(name, 0))
        {
            return null;
        }

        PlaceholderRemovalResult removed;
        try
        {
            removed = RemovePlaceholders(name);
            _logger.LogInformation(
                "Reset {Library}: removed {Movies} movies, {Episodes} episodes, {Seasons} seasons, {Series} series; fetching the newest items",
                name,
                removed.Movies,
                removed.Episodes,
                removed.Seasons,
                removed.Series);
        }
        catch
        {
            _running.TryRemove(name, out _);
            throw;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RefreshAsync(name, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Refreshing {Library} after a reset failed", name);
                }
                finally
                {
                    _running.TryRemove(name, out _);
                }
            },
            CancellationToken.None);

        return removed;
    }

    private static bool IsRemovable(BaseItem item)
        => !item.IsDownloaded && item is IPirateFinDownloadable { DownloadStartedUtc: null };

    private IEnumerable<T> Query<T>(PirateFinLibrary library, BaseItemKind kind, bool isInternet)
        => _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = library.CollectionFolderId,
                Recursive = true,
                IncludeItemTypes = [kind],
                IsInternet = isInternet
            })
            .OfType<T>()
            .ToList();

    private bool IsEmpty(BaseItem folder)
        => _libraryManager.GetItemList(new InternalItemsQuery { ParentId = folder.Id, Limit = 1 }).Count == 0;

    private bool Delete(BaseItem item)
    {
        try
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove {Name} ({Id})", item.Name, item.Id);
            return false;
        }
    }

    // The folder only goes when it's empty, so nothing the user put there (or a stray partial download) is lost.
    private bool DeleteWithEmptyFolder(BaseItem folder)
    {
        if (!Delete(folder))
        {
            return false;
        }

        try
        {
            if (!string.IsNullOrEmpty(folder.Path) && Directory.Exists(folder.Path) && !Directory.EnumerateFileSystemEntries(folder.Path).Any())
            {
                Directory.Delete(folder.Path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remove the folder {Path}", folder.Path);
        }

        return true;
    }
}
