using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

public interface IDownloadUrlService
{
    Task<EpisodeDownloadCandidate?> GetDownloadUrlAsync(Episode episode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EpisodeDownloadCandidate>> GetBulkDownloadUrlsAsync(string animeTitle, int limit = 100, CancellationToken cancellationToken = default);

    Task<Dictionary<int, Dictionary<int, EpisodeDownloadCandidate>>> GetTVShowBulkDownloadUrlsAsync(SearchedSeries series, string library, int limit = 100, CancellationToken cancellationToken = default);
}

public sealed record EpisodeDownloadCandidate(string Title, string MagnetUrl, int Seeders, int Episode = 0);
