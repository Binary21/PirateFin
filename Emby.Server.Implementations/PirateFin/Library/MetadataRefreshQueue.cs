using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Library;

/// <summary>
/// Background metadata refresh for generated placeholders. Each job refreshes its parent first (so seasons /
/// episodes can use the series' provider ids), then its children with bounded parallelism. Jobs queued with
/// <see cref="EnqueueFirst"/> go ahead of the others; big jobs are split into small batches so they never have to wait
/// long for one already running.
/// </summary>
public sealed class MetadataRefreshQueue : BackgroundService, IMetadataRefreshQueue
{
    private const int ChildConcurrency = 4;
    private const int BatchSize = 8;

    private readonly ConcurrentQueue<(Guid? ParentId, Guid[] ChildIds)> _first = new();
    private readonly ConcurrentQueue<(Guid? ParentId, Guid[] ChildIds)> _jobs = new();
    private readonly SemaphoreSlim _queued = new(0);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MetadataRefreshQueue> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataRefreshQueue"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public MetadataRefreshQueue(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger<MetadataRefreshQueue> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Enqueue(BaseItem? parent, IReadOnlyList<BaseItem> children) => Add(_jobs, parent, children);

    /// <inheritdoc />
    public void EnqueueFirst(BaseItem? parent, IReadOnlyList<BaseItem> children) => Add(_first, parent, children);

    /// <inheritdoc />
    public override void Dispose()
    {
        _queued.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _queued.WaitAsync(stoppingToken).ConfigureAwait(false);
                if (!_first.TryDequeue(out var job) && !_jobs.TryDequeue(out job))
                {
                    continue;
                }

                if (job.ParentId.HasValue)
                {
                    await RefreshAsync(job.ParentId.Value, stoppingToken).ConfigureAwait(false);
                }

                await Parallel.ForEachAsync(
                    job.ChildIds,
                    new ParallelOptions { MaxDegreeOfParallelism = ChildConcurrency, CancellationToken = stoppingToken },
                    async (id, ct) => await RefreshAsync(id, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // The parent goes alone first, then the children in batches, in order.
    private void Add(ConcurrentQueue<(Guid? ParentId, Guid[] ChildIds)> queue, BaseItem? parent, IReadOnlyList<BaseItem> children)
    {
        if (parent is not null)
        {
            queue.Enqueue((parent.Id, []));
            _queued.Release();
        }

        foreach (var batch in children.Select(c => c.Id).Chunk(BatchSize))
        {
            queue.Enqueue((null, batch));
            _queued.Release();
        }
    }

    private async Task RefreshAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return;
        }

        try
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = true,
                ForceSave = true,
                RemoveOldMetadata = true,
                EnableRemoteContentProbe = false
            };
            await _providerManager.RefreshFullItem(item, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Metadata refresh failed for {Name} ({Id})", item.Name, item.Id);
        }
    }
}
