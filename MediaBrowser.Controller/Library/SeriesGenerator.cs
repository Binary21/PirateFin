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
    public class SeriesGenerator : ISeriesGenerator
    {
        private readonly ILogger<SeriesGenerator> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;

        private readonly ISubscriptionService _subscriptionService;

        private static char[] invalidChars = Path.GetInvalidFileNameChars();

        public SeriesGenerator(ILogger<SeriesGenerator> logger, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ISubscriptionService subscriptionService)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _subscriptionService = subscriptionService;
        }

        public async Task GenerateSeries(SearchedSeries searchedSeries, string library)
        {
            if (searchedSeries.Title.Length == 0)
            {
                return;
            }

            var resolvedLibrary = _libraryManager.GetVirtualFolders().FirstOrDefault(vf => vf.Name.Equals(library, StringComparison.OrdinalIgnoreCase));

            if (resolvedLibrary == null)
            {
                _logger.LogError("No {Library} Library found for Series Generation.", library);
                return;
            }

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

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = true,
                ReplaceAllMetadata = true,
                ForceSave = true,
                IsAutomated = false,
                RemoveOldMetadata = true,
                EnableRemoteContentProbe = false
            };

            await GetOrGenerateSeries(searchedSeries, libraryPath, parentItem, refreshOptions, library).ConfigureAwait(false);
        }

        private async Task GetOrGenerateSeries(SearchedSeries searchedSeries, string libraryPath, BaseItem parentItem, MetadataRefreshOptions refreshOptions, string library)
        {
            var sanitizedName = new string(searchedSeries.Title.Where(ch => !invalidChars.Contains(ch)).ToArray());
            var seriesPath = Path.Combine(libraryPath, sanitizedName);
            var seriesId = _libraryManager.GetNewItemId(seriesPath, typeof(Series));
            var series = _libraryManager.GetItemById(seriesId);
            bool newSeries = series == null || Equals(series.ParentId, Guid.Empty);
            var variableRefreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
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
                    Genres = searchedSeries.Genres
                };
                if (!string.IsNullOrEmpty(searchedSeries.AniListId))
                {
                    series.ProviderIds["AniList"] = searchedSeries.AniListId;
                }

                _libraryManager.CreateItem(series, parentItem);
                _logger.LogInformation("Refreshing series metadata for {Name}", searchedSeries.Title);
                await _providerManager.RefreshFullItem(series, refreshOptions, CancellationToken.None).ConfigureAwait(false);
            }

            foreach (SearchedSeason searchedSeason in searchedSeries.Seasons)
            {
                await GetOrGenerateSeason(searchedSeason, seriesPath, sanitizedName, series, seriesId, newSeries, refreshOptions, library).ConfigureAwait(false);
            }

            if (newSeries)
            {
                _logger.LogInformation("Scheduling background refresh for Series with metadata providers");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _providerManager.RefreshFullItem(series, variableRefreshOptions, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error refreshing series with metadata providers for {Name}", sanitizedName);
                    }
                });
            }

            _ = Task.Run(() => _subscriptionService.ProcessBulkEpisodeAsync(searchedSeries, series, library, CancellationToken.None));
        }

        private async Task GetOrGenerateSeason(SearchedSeason searchedSeason, string seriesPath, string sanitizedName, BaseItem seriesItem, Guid seriesId, bool newSeries, MetadataRefreshOptions refreshOptions, string library)
        {
            var seasonPath = Path.Combine(seriesPath, searchedSeason.Title);
            var seasonId = _libraryManager.GetNewItemId(seasonPath, typeof(Season));
            var season = _libraryManager.GetItemById(seasonId);
            bool newSeason = season == null || Equals(season.ParentId, Guid.Empty);
            var variableRefreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
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

            if (newSeason)
            {
                _logger.LogInformation("[SeasonErrorEvaluation] Creating season {Season} for {Name}, Path: {Path}, Id: {Id}, IsInternet: {IsInternet}, IsScheduled {Scheduled}", searchedSeason.Season, sanitizedName, seasonPath, seasonId, true, true);
                if (!Directory.Exists(seasonPath))
                {
                    Directory.CreateDirectory(seasonPath);
                }

                season = new Season
                {
                    Id = seasonId,
                    Name = searchedSeason.Title,
                    IndexNumber = searchedSeason.Season,
                    Path = seasonPath,
                    SeriesId = seriesId,
                    SeriesName = sanitizedName,
                    ParentId = seriesId,
                    Scheduled = true,
                    IsInternet = true,
                    Subscribed = false,
                    Genres = searchedSeason.Genres,
                    Overview = searchedSeason.Overview
                };
                _libraryManager.CreateItem(season, seriesItem);

                _logger.LogInformation("Refreshing season metadata for {Name} Season {Season}", searchedSeason.Title, searchedSeason.Season);
                await _providerManager.RefreshFullItem(season, refreshOptions, CancellationToken.None).ConfigureAwait(false);
                var postRefreshItem = _libraryManager.GetItemById(seasonId);
                if (postRefreshItem is Season seasonReturn)
                        {
                            _logger.LogInformation("[SeasonErrorEvaluation] Evaluating for {Name}, Path: {Path}, Id: {Id}, IsInternet: {IsInternet}, IsScheduled {Scheduled}", seasonReturn.SeriesName, seasonReturn.Path, seasonReturn.Id, seasonReturn.IsInternet, seasonReturn.Scheduled);
                        }
            }

            List<BaseItem> generatedEpisodes = [];

            if (season is not Season seasonType)
            {
                return;
            }

            HashSet<int> existingEpisodeNumbers = seasonType
                .GetEpisodes()
                .OfType<Episode>()
                .Where(e => e.IndexNumber.HasValue)
                .Select(e => e.IndexNumber!.Value)
                .ToHashSet();

            foreach (SearchedEpisode searchedEpisode in searchedSeason.Episodes)
            {
                if (existingEpisodeNumbers.Contains(searchedEpisode.Episode))
                {
                    continue;
                }

                BaseItem? tempBaseItem = await GetOrGenerateEpisode(searchedEpisode, searchedSeason, seasonPath, sanitizedName, season, seriesItem, seasonId, seriesId, refreshOptions, library).ConfigureAwait(false);
                if (tempBaseItem != null)
                {
                    generatedEpisodes.Add(tempBaseItem);
                }
            }

            _logger.LogInformation("Scheduling background refresh for episodes metadata S{Season} for {SeriesName}", searchedSeason.Season, sanitizedName);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _providerManager.RefreshFullItems(generatedEpisodes, newSeason || newSeries ? refreshOptions : variableRefreshOptions, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error refreshing episodes metadata S{Season} for {SeriesName}", searchedSeason.Season, sanitizedName);
                }
            });

            if (newSeason && !newSeries)
            {
                _logger.LogInformation("Scheduling background refresh for Season with metadata providers");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _providerManager.RefreshFullItem(season, variableRefreshOptions, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error refreshing season with metadata providers for {Name} Season {Season}", searchedSeason.Title, searchedSeason.Season);
                    }
                });
            }
        }

        private async Task<BaseItem?> GetOrGenerateEpisode(SearchedEpisode searchedEpisode, SearchedSeason searchedSeason, string seasonPath, string sanitizedName, BaseItem seasonItem, BaseItem seriesItem, Guid seasonId, Guid seriesId, MetadataRefreshOptions refreshMetadata, string library)
        {
            var episodeFileName = $"{sanitizedName} - S{searchedSeason.Season:00}E{searchedEpisode.Episode:00}.mkv";
            var episodePath = Path.Combine(seasonPath, episodeFileName);
            var episodeId = _libraryManager.GetNewItemId(episodePath, typeof(Episode));
            var existingEpisode = _libraryManager.GetItemById(episodeId);
            var santitizedAlternateTitle = new string(searchedSeason.RomajiTitle.Where(ch => !invalidChars.Contains(ch)).ToArray());

            if (existingEpisode == null)
            {
                _logger.LogInformation("Creating episode S{Season}E{Episode} for {Name}", searchedSeason.Season, searchedEpisode.Episode, sanitizedName);
                var tempItem = new Episode
                {
                    Id = episodeId,
                    IsPlaceHolder = true,
                    Scheduled = true,
                    Name = sanitizedName,
                    OriginalTitle = searchedEpisode.Title ?? searchedSeason.AlternateTitle,
                    AlternateTitle = santitizedAlternateTitle,
                    FirstAiring = searchedEpisode.FirstAiring,
                    IsInternet = true,
                    Path = episodePath,
                    BackupImageUrl = searchedSeason.ImageUrl,
                    DownloadUrl = null,
                    ParentIndexNumber = searchedSeason.Season,
                    IndexNumber = searchedEpisode.Episode,
                    SeriesId = seriesId,
                    SeasonId = seasonId,
                    SeriesName = sanitizedName,
                    SeasonName = searchedSeason.Title,
                    ParentId = seasonId,
                    IsDownloadable = false,
                    IsDownloaded = false,
                    // No physical file exists yet; kept true until DownloadManager confirms real media on disk, so ffprobe/image providers skip this item.
                    IsVirtualItem = true,
                    LibraryType = library,
                    Genres = searchedSeason.Genres,
                    Overview = searchedEpisode.Overview
                };
                _libraryManager.CreateItem(tempItem, seasonItem);
                return tempItem;
            }

            return null;
        }
    }
}
