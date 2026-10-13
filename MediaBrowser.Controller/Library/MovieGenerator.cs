using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Generates a movie as well as its path.
    /// </summary>
    public class MovieGenerator : IMovieGenerator
    {
        private readonly ILogger<MovieGenerator> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private static char[] invalidChars = Path.GetInvalidFileNameChars();

        public MovieGenerator(ILogger<MovieGenerator> logger, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        public async Task GenerateMovie(List<ScheduleMovie> scheduleMovies, string library)
        {
            if (scheduleMovies.Count == 0)
            {
                return;
            }

            // TODO: Check for storage device size. If full default to next storage location. If all locations full email admin.
            var resolvedLibrary = _libraryManager.GetVirtualFolders().FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));

            if (resolvedLibrary == null)
            {
                _logger.LogError("No {Library} Library found for Schedule Generator.", library);
                return;
            }

            // TODO: Check for storage device size. If full default to next storage location. If all locations full email admin.
            string? libraryPath = resolvedLibrary.Locations.FirstOrDefault();
            if (string.IsNullOrEmpty(libraryPath))
            {
                _logger.LogError("No {Library} Library path configured, please configure the path for this library.", library);
                return;
            }

            var parentItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Path = libraryPath,
                IsFolder = true,
            });
            var parentItem = parentItems.Count > 0 ? parentItems[0] : null;

            if (parentItem == null)
            {
                _logger.LogError("No {Library} Library parent Item could not be found", library);
                return;
            }

            var refreshMetadata = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                ReplaceAllMetadata = true,
                ForceSave = true,
                IsAutomated = false,
                RemoveOldMetadata = true,
                EnableRemoteContentProbe = false
            };

            foreach (ScheduleMovie scheduleMovie in scheduleMovies)
            {
                await GetOrGenerateMovie(scheduleMovie, libraryPath, parentItem, refreshMetadata).ConfigureAwait(false);
            }
        }

        private async Task GetOrGenerateMovie(ScheduleMovie scheduleMovie, string libraryPath, BaseItem parentItem, MetadataRefreshOptions refreshMetadata)
        {
            var episodeFileName = new string(scheduleMovie.AlternateTitle.Where(ch => !invalidChars.Contains(ch)).ToArray()) + ".mkv";
            var moviePath = Path.Combine(libraryPath, episodeFileName);
            var movieId = _libraryManager.GetNewItemId(moviePath, typeof(Movie));
            var existingMovie = _libraryManager.GetItemById(movieId);
            var santitizedAlternateTitle = new string(scheduleMovie.AlternateTitle.Where(ch => !invalidChars.Contains(ch)).ToArray());

            if (existingMovie == null)
            {
                _logger.LogInformation("Creating Movie {Name}", episodeFileName);
                var tempItem = new Movie
                {
                    Id = movieId,
                    IsPlaceHolder = true,
                    Scheduled = true,
                    Name = santitizedAlternateTitle,
                    OriginalTitle = santitizedAlternateTitle,
                    FirstAiring = DateTime.Now,
                    IsInternet = true,
                    Path = moviePath,
                    BackupImageUrl = scheduleMovie.ImageUrl,
                    DownloadUrl = scheduleMovie.MagnetUrl,
                    ParentId = parentItem.Id,
                    IsDownloadable = string.IsNullOrEmpty(scheduleMovie.MagnetUrl) ? false : true,
                    IsDownloaded = false,
                    // No physical file exists yet; kept true until DownloadManager confirms real media on disk, so ffprobe/image providers skip this item.
                    IsVirtualItem = true,
                    Genres = scheduleMovie.Genres,
                };
                _libraryManager.CreateItem(tempItem, parentItem);

                _logger.LogInformation("[RefreshNewestShowsTask] Refreshing movie metadata for {SeriesName}", santitizedAlternateTitle);
                await _providerManager.RefreshFullItem(tempItem, refreshMetadata, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
