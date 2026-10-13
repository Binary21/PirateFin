using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

public sealed class DownloadUrlService : IDownloadUrlService
{
    private readonly JackettSearchService _jackettSearchService;
    private readonly ILogger<DownloadUrlService> _logger;

    public DownloadUrlService(JackettSearchService jackettSearchService, ILogger<DownloadUrlService> logger)
    {
        _jackettSearchService = jackettSearchService;
        _logger = logger;
    }

    public Task<IReadOnlyList<EpisodeDownloadCandidate>> GetBulkDownloadUrlsAsync(string animeTitle, int limit = 100, CancellationToken cancellationToken = default)
        => _jackettSearchService.SearchBulkAsync(animeTitle, limit, cancellationToken);

    public Task<Dictionary<int, Dictionary<int, EpisodeDownloadCandidate>>> GetTVShowBulkDownloadUrlsAsync(SearchedSeries series, string library, int limit = 100, CancellationToken cancellationToken = default)
        => _jackettSearchService.SeachTVShowsBulkAsync(series, library, limit, cancellationToken);

    public async Task<EpisodeDownloadCandidate?> GetDownloadUrlAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var candidate = await _jackettSearchService.FindBestMatchAsync(episode, cancellationToken).ConfigureAwait(false);
        if (candidate is not null)
        {
            _logger.LogInformation("Found download URL for episode {EpisodeId}", episode.Id);
        }

        return candidate;
    }
}
