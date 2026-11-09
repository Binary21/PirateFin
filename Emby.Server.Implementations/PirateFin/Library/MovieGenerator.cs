using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Storage;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Creates placeholder movies from YTS.
/// </summary>
public sealed class MovieGenerator : IMovieGenerator
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMetadataRefreshQueue _refreshQueue;
    private readonly LibraryStorage _storage;
    private readonly ILogger<MovieGenerator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieGenerator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="refreshQueue">The metadata refresh queue.</param>
    /// <param name="storage">Picks the library folder (drive) new movies go to.</param>
    /// <param name="logger">The logger.</param>
    public MovieGenerator(ILibraryManager libraryManager, IMetadataRefreshQueue refreshQueue, LibraryStorage storage, ILogger<MovieGenerator> logger)
    {
        _libraryManager = libraryManager;
        _refreshQueue = refreshQueue;
        _storage = storage;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task GenerateMoviesAsync(IReadOnlyList<ScheduleMovie> scheduleMovies, string library, CancellationToken cancellationToken)
    {
        if (scheduleMovies.Count == 0)
        {
            return;
        }

        var target = PirateFinLibrary.Resolve(_libraryManager, library, _logger);
        if (target is null)
        {
            return;
        }

        foreach (var scheduleMovie in scheduleMovies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileTitle = PirateFinLibrary.SanitizeFileName(scheduleMovie.FileTitle);
            if (fileTitle.Length == 0)
            {
                continue;
            }

            if (FindExisting(target, fileTitle) is { } existing)
            {
                await UpdateExistingAsync(existing, GetLocation(target, existing.Path).Folder, scheduleMovie, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // New movies go to the library folder whose drive has the most room.
            var location = _storage.ChooseNewItemLocation(target);
            var path = GetPath(location, fileTitle);

            _logger.LogInformation("Creating movie {Name}", fileTitle);
            var movie = new Movie
            {
                Id = _libraryManager.GetNewItemId(path, typeof(Movie)),
                Name = scheduleMovie.Title,
                OriginalTitle = scheduleMovie.Title,
                ProductionYear = scheduleMovie.Year > 0 ? scheduleMovie.Year : null,
                Overview = scheduleMovie.Overview,
                FirstAiring = scheduleMovie.FirstAiring ?? DateTime.UtcNow,
                Path = path,
                BackupImageUrl = scheduleMovie.ImageUrl,
                DownloadUrl = scheduleMovie.MagnetUrl,
                IsDownloadable = !string.IsNullOrEmpty(scheduleMovie.MagnetUrl),
                ParentId = location.Folder.Id,
                Genres = scheduleMovie.Genres,
                IsInternet = true,
                Scheduled = true,
                IsPlaceHolder = true,
                IsDownloaded = false,
                IsVirtualItem = true
            };

            // Without an id, TMDb looks the movie up by name and takes its first hit, which for titles it doesn't
            // know (concert films, new releases) is an unrelated movie. The IMDb id gives an exact match or none.
            if (!string.IsNullOrEmpty(scheduleMovie.ImdbId))
            {
                movie.SetProviderId(MetadataProvider.Imdb, scheduleMovie.ImdbId);
            }

            _libraryManager.CreateItem(movie, location.Folder);
            _refreshQueue.Enqueue(movie, []);
        }
    }

    /// <inheritdoc />
    public Guid? FindExistingMovieId(ScheduleMovie scheduleMovie, string library)
    {
        var target = PirateFinLibrary.Resolve(_libraryManager, library, _logger);
        var fileTitle = PirateFinLibrary.SanitizeFileName(scheduleMovie.FileTitle);
        if (target is null || fileTitle.Length == 0)
        {
            return null;
        }

        return FindExisting(target, fileTitle)?.Id;
    }

    // Each movie gets its own folder, "<library folder>/<Title Year>/<Title Year>.mkv", which the download manager creates
    // when the download starts. The path's hash is the item id, so it must stay stable.
    private static string GetPath(PirateFinLocation location, string fileTitle)
        => Path.Combine(location.Path, fileTitle, fileTitle + ".mkv");

    // Placeholders made before movies got their own folder are "<library folder>/<Title Year>.mkv"; they keep that path
    // (and id), and download there.
    private static string GetLegacyPath(PirateFinLocation location, string fileTitle)
        => Path.Combine(location.Path, fileTitle + ".mkv");

    private static PirateFinLocation GetLocation(PirateFinLibrary library, string path)
        => library.Locations.FirstOrDefault(l => path.StartsWith(l.Path, StringComparison.OrdinalIgnoreCase)) ?? library.Locations[0];

    // The movie in any of the library's folders (a library can span drives).
    private Movie? FindExisting(PirateFinLibrary library, string fileTitle)
        => library.Locations
            .SelectMany(location => new[] { GetPath(location, fileTitle), GetLegacyPath(location, fileTitle) })
            .Select(path => _libraryManager.GetItemById(_libraryManager.GetNewItemId(path, typeof(Movie))))
            .OfType<Movie>()
            .FirstOrDefault(movie => !movie.ParentId.IsEmpty());

    private async Task UpdateExistingAsync(Movie existing, BaseItem parent, ScheduleMovie scheduleMovie, CancellationToken cancellationToken)
    {
        if (!existing.IsInternet)
        {
            return;
        }

        var changed = false;
        if (string.IsNullOrEmpty(existing.DownloadUrl) && !string.IsNullOrEmpty(scheduleMovie.MagnetUrl))
        {
            existing.DownloadUrl = scheduleMovie.MagnetUrl;
            existing.IsDownloadable = true;
            changed = true;
        }

        // Placeholders created before the IMDb id was used may have been matched to the wrong movie by name.
        // Start over from the YTS data with the exact id; the queued refresh then fetches the right metadata.
        var rematch = !string.IsNullOrEmpty(scheduleMovie.ImdbId)
            && !string.Equals(existing.GetProviderId(MetadataProvider.Imdb), scheduleMovie.ImdbId, StringComparison.OrdinalIgnoreCase);
        if (rematch)
        {
            _logger.LogInformation("Re-matching {Name} using IMDb id {ImdbId}", scheduleMovie.Title, scheduleMovie.ImdbId);
            existing.Name = scheduleMovie.Title;
            existing.OriginalTitle = scheduleMovie.Title;
            existing.Overview = scheduleMovie.Overview;
            existing.Genres = scheduleMovie.Genres;
            existing.ProductionYear = scheduleMovie.Year > 0 ? scheduleMovie.Year : null;
            existing.BackupImageUrl = scheduleMovie.ImageUrl;
            existing.ProviderIds.Clear();
            existing.SetProviderId(MetadataProvider.Imdb, scheduleMovie.ImdbId);
            existing.ImageInfos = [];
            changed = true;
        }

        if (changed)
        {
            await _libraryManager.UpdateItemAsync(existing, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        if (rematch)
        {
            _refreshQueue.Enqueue(existing, []);
        }
    }
}
