using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

public interface IDownloadManager
{
    Task QueueDownload(Episode episode, CancellationToken cancellationToken = default);
}
