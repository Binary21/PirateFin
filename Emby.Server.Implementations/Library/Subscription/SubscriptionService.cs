using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SubscriptionService : ISubscriptionService
{
    private readonly IEpisodeSubscriptionRepository _repository;
    private readonly IDownloadUrlService _downloadUrlService;
    private readonly IDownloadManager _downloadManager;
    private readonly ILogger<SubscriptionService> _logger;
    private readonly SubscriptionOptions _options;

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public SubscriptionService(
        IEpisodeSubscriptionRepository repository,
        IDownloadUrlService downloadUrlService,
        IDownloadManager downloadManager,
        IOptions<SubscriptionOptions> options,
        ILogger<SubscriptionService> logger)
    {
        _repository = repository;
        _downloadUrlService = downloadUrlService;
        _downloadManager = downloadManager;
        _logger = logger;
        _options = options.Value;
    }

    public async Task RunAsync(CancellationToken cancellationToken )
    {
        // Single loop, no per-episode timers.
        using var timer = new PeriodicTimer(_options.ScanInterval);

        do
        {
            await ProcessDueEpisodesAsync(cancellationToken ).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken ).ConfigureAwait(false));
    }

    public async Task ProcessDueEpisodesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var start = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await _repository.GetCandidatesAsync(now, _options.PageSize, start, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            // Bounded parallelism with dedupe.
            await Parallel.ForEachAsync(
                page,
                new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency, CancellationToken = cancellationToken },
                async (episode, token) =>
                {
                    if (!_inFlight.TryAdd(episode.Id, 0))
                    {
                        return;
                    }

                    try
                    {
                        var candidate = await _downloadUrlService.GetDownloadUrlAsync(episode, token).ConfigureAwait(false);
                        if (candidate is null || string.IsNullOrWhiteSpace(candidate.MagnetUrl))
                        {
                            return;
                        }

                        await _repository.PersistDownloadUrlAsync(episode, candidate, token).ConfigureAwait(false);

                        if (await _repository.IsSeasonSubscribedAsync(episode, token).ConfigureAwait(false))
                        {
                            await _downloadManager.QueueDownload(episode, token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed processing episode {EpisodeId}", episode.Id);
                    }
                    finally
                    {
                        _inFlight.TryRemove(episode.Id, out _);
                    }
                }).ConfigureAwait(false);

            start += page.Count;
        }
    }

    public async Task ProcessBulkEpisodeAsync(SearchedSeries searchedSeries, BaseItem series, string library, CancellationToken cancellationToken)
    {
        if (series is not Series seriesItem)
        {
            return;
        }

        try
        {
            Dictionary<int, Dictionary<int, EpisodeDownloadCandidate>> candidates = await _downloadUrlService.GetTVShowBulkDownloadUrlsAsync(searchedSeries, library, 100, cancellationToken).ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                return;
            }

            foreach (Season season in seriesItem.Children.OfType<Season>())
            {
                if (!candidates.TryGetValue(season.IndexNumber ?? 0, out var seasonEpisodes))
                {
                    continue;
                }

                foreach (Episode episode in season.Children.OfType<Episode>())
                {
                    if (seasonEpisodes.TryGetValue(episode.IndexNumber ?? 0, out var candidate) && !string.IsNullOrWhiteSpace(candidate.MagnetUrl))
                    {
                        await _repository.PersistDownloadUrlAsync(episode, candidate, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed processing series {EpisodeId}", seriesItem.Name);
        }
    }
}
