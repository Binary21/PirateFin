using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Generates an episode as well as its path.
    /// </summary>
    public class EpisodeGenerator : IEpisodeGenerator
    {
        private readonly ILogger<EpisodeGenerator> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;

        private static char[] invalidChars = Path.GetInvalidFileNameChars();

        public EpisodeGenerator(ILogger<EpisodeGenerator> logger, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        public async Task GenerateEpisode(List<ScheduleEpisode> scheduleEpisodes, string library, DateTime startOfWeek)
        {
            if (scheduleEpisodes.Count == 0)
            {
                return;
            }

            // TODO: Check for storage device size. If full default to next storage location. If all locations full email admin.
            var resolvedLibrary = _libraryManager.GetVirtualFolders().FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));

            if (resolvedLibrary == null)
            {
                _logger.LogError("No {Library} Library found for Schedule Generation.", library);
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

            foreach (ScheduleEpisode scheduleEpisode in scheduleEpisodes)
            {
                if (scheduleEpisode.AiringStatus != "NOT_YET_RELEASED")
                {
                    await GetOrGenerateSeries(scheduleEpisode, libraryPath, parentItem, refreshMetadata, startOfWeek, library).ConfigureAwait(false);
                }
            }
        }

        private async Task GetOrGenerateSeries(ScheduleEpisode scheduleEpisode, string libraryPath, BaseItem parentItem, MetadataRefreshOptions refreshMetadata, DateTime startOfWeek, string library)
        {
            var sanitizedName = new string(scheduleEpisode.Title.Where(ch => !invalidChars.Contains(ch)).ToArray());
            var seriesPath = Path.Combine(libraryPath, sanitizedName);
            var seriesId = _libraryManager.GetNewItemId(seriesPath, typeof(Series));
            var series = _libraryManager.GetItemById(seriesId);

            if (series == null || Equals(series.ParentId, Guid.Empty))
            {
                _logger.LogError("Creating New Series: {Name} in {Path}", sanitizedName, seriesPath);
                if (!Directory.Exists(seriesPath))
                {
                    Directory.CreateDirectory(seriesPath);
                }

                series = new Series
                {
                    Id = seriesId,
                    Name = sanitizedName,
                    Path = seriesPath,
                    IsInternet = true,
                    ParentId = parentItem.Id,
                    Genres = scheduleEpisode.Genres,
                };
                _libraryManager.CreateItem(series, parentItem);
                _logger.LogInformation("Refreshing series metadata for {Name}", scheduleEpisode.Title);
                await _providerManager.RefreshFullItem(series, refreshMetadata, CancellationToken.None).ConfigureAwait(false);
            }

            await GetOrGenerateSeason(scheduleEpisode, seriesPath, sanitizedName, series, seriesId, refreshMetadata, startOfWeek, library).ConfigureAwait(false);
        }

        private async Task GetOrGenerateSeason(ScheduleEpisode scheduleEpisode, string seriesPath, string sanitizedName, BaseItem seriesItem, Guid seriesId, MetadataRefreshOptions refreshMetadata, DateTime startOfWeek, string library)
        {
            var seasonPath = Path.Combine(seriesPath, $"Season {scheduleEpisode.Season}");
            var seasonId = _libraryManager.GetNewItemId(seasonPath, typeof(Season));
            var season = _libraryManager.GetItemById(seasonId);

            if (season == null || Equals(season.ParentId, Guid.Empty))
            {
                _logger.LogInformation("[SeasonErrorEvaluation] Creating season {Season} for {Name}, Path: {Path}, Id: {Id}, IsInternet: {IsInternet}, IsScheduled {Scheduled}", scheduleEpisode.Season, sanitizedName, seasonPath, seasonId, true, true);
                if (!Directory.Exists(seasonPath))
                {
                    Directory.CreateDirectory(seasonPath);
                }

                season = new Season
                {
                    Id = seasonId,
                    Name = $"Season {scheduleEpisode.Season}",
                    IndexNumber = scheduleEpisode.Season,
                    Path = seasonPath,
                    SeriesId = seriesId,
                    SeriesName = sanitizedName,
                    ParentId = seriesId,
                    Scheduled = true,
                    IsInternet = true,
                    Subscribed = scheduleEpisode.Subscribed,
                    Genres = scheduleEpisode.Genres,
                };
                _libraryManager.CreateItem(season, seriesItem);
                _logger.LogInformation("Refreshing season metadata for {Name} Season {Season}", scheduleEpisode.Title, scheduleEpisode.Season);
                await _providerManager.RefreshFullItem(season, refreshMetadata, CancellationToken.None).ConfigureAwait(false);
                var postRefreshItem = _libraryManager.GetItemById(seasonId);
                if (postRefreshItem is Season seasonReturn)
                {
                    _logger.LogInformation("[SeasonErrorEvaluation] Evaluating for {Name}, Path: {Path}, Id: {Id}, IsInternet: {IsInternet}, IsScheduled {Scheduled}", seasonReturn.SeriesName, seasonReturn.Path, seasonReturn.Id, seasonReturn.IsInternet, seasonReturn.Scheduled);
                }
            }

            await GetOrGenerateEpisode(scheduleEpisode, seasonPath, sanitizedName, season, seriesItem, seasonId, seriesId, refreshMetadata, startOfWeek, library).ConfigureAwait(false);
        }

        private async Task GetOrGenerateEpisode(ScheduleEpisode scheduleEpisode, string seasonPath, string sanitizedName, BaseItem seasonItem, BaseItem seriesItem, Guid seasonId, Guid seriesId, MetadataRefreshOptions refreshMetadata, DateTime startOfWeek, string library)
        {
            var episodeFileName = $"{sanitizedName} - S{scheduleEpisode.Season:00}E{scheduleEpisode.Number ?? 0:00}.mkv";
            var episodePath = Path.Combine(seasonPath, episodeFileName);
            var episodeId = _libraryManager.GetNewItemId(episodePath, typeof(Episode));
            var existingEpisode = _libraryManager.GetItemById(episodeId);
            var santitizedAlternateTitle = new string(scheduleEpisode.AlternateTitle.Where(ch => !invalidChars.Contains(ch)).ToArray());

            if (existingEpisode == null)
            {
                _logger.LogInformation("Creating episode S{Season}E{Episode} for {Name}", scheduleEpisode.Season, scheduleEpisode.Number, sanitizedName);
                var tempItem = new Episode
                {
                    Id = episodeId,
                    IsPlaceHolder = true,
                    Scheduled = true,
                    Name = sanitizedName,
                    OriginalTitle = scheduleEpisode.Title,
                    AlternateTitle = santitizedAlternateTitle,
                    FirstAiring = scheduleEpisode.FirstAiring,
                    IsInternet = true,
                    Path = episodePath,
                    BackupImageUrl = scheduleEpisode.ImageUrl,
                    DownloadUrl = scheduleEpisode.MagnetUrl,
                    ParentIndexNumber = scheduleEpisode.Season,
                    IndexNumber = scheduleEpisode.Number,
                    SeriesId = seriesId,
                    SeasonId = seasonId,
                    SeriesName = sanitizedName,
                    SeasonName = $"Season {scheduleEpisode.Season}",
                    ParentId = seasonId,
                    IsDownloadable = string.IsNullOrEmpty(scheduleEpisode.MagnetUrl) ? false : true,
                    IsDownloaded = false,
                    EpisodeReleaseWeek = startOfWeek,
                    // No physical file exists yet; kept true until DownloadManager confirms real media on disk, so ffprobe/image providers skip this item.
                    IsVirtualItem = true,
                    LibraryType = library,
                    Genres = scheduleEpisode.Genres,
                };
                _libraryManager.CreateItem(tempItem, seasonItem);

                _logger.LogInformation("[RefreshNewestShowsTask] Refreshing episode metadata S{Season}E{Episode} for {SeriesName}", scheduleEpisode.Season, scheduleEpisode.Number, sanitizedName);
                await _providerManager.RefreshFullItem(tempItem, refreshMetadata, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
