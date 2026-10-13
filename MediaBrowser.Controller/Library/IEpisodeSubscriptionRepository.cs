using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

public interface IEpisodeSubscriptionRepository
{
    Task<IReadOnlyList<Episode>> GetCandidatesAsync(DateTime utcNow, int pageSize, int startIndex, CancellationToken ct);

    Task<bool> IsSeasonSubscribedAsync(Episode episode, CancellationToken ct);

    Task PersistDownloadUrlAsync(Episode episode, EpisodeDownloadCandidate downloadCandidate, CancellationToken ct);
}
