using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

public interface ISubscriptionService
{
    Task RunAsync(CancellationToken cancellationToken);

    Task ProcessDueEpisodesAsync(CancellationToken cancellationToken);

    Task ProcessBulkEpisodeAsync(SearchedSeries searchedSeries, BaseItem series, string library, CancellationToken cancellationToken);
}
