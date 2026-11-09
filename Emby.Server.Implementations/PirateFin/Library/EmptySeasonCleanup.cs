using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Removes the empty "Season Unknown" seasons left behind when a metadata refresh briefly cleared PirateFin episodes'
/// season numbers: seasons Jellyfin created itself (no number, no folder) that hold no episodes.
/// </summary>
public sealed class EmptySeasonCleanup : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<EmptySeasonCleanup> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmptySeasonCleanup"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public EmptySeasonCleanup(ILibraryManager libraryManager, ILogger<EmptySeasonCleanup> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var empty = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Season],
                    Recursive = true
                })
                .OfType<Season>()
                .Where(s => !s.IndexNumber.HasValue && string.IsNullOrEmpty(s.Path) && s.GetEpisodes().Count == 0)
                .ToList();

            foreach (var season in empty)
            {
                _logger.LogInformation("Removing the empty {Season} of {Series}", season.Name, season.SeriesName);
                _libraryManager.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false }, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the empty seasons");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
