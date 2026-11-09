using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Fetches metadata for generated placeholders in the background so requests never wait on metadata providers.
/// </summary>
public interface IMetadataRefreshQueue
{
    /// <summary>
    /// Queues a refresh. <paramref name="parent"/> (if any) is refreshed first so its provider ids are available, then <paramref name="children"/> in parallel.
    /// </summary>
    /// <param name="parent">The series or movie to refresh first, or <c>null</c>.</param>
    /// <param name="children">Seasons / episodes to refresh afterwards.</param>
    void Enqueue(BaseItem? parent, IReadOnlyList<BaseItem> children);

    /// <summary>
    /// Queues a refresh ahead of everything else waiting (e.g. the season the user is looking at).
    /// </summary>
    /// <param name="parent">The series or movie to refresh first, or <c>null</c>.</param>
    /// <param name="children">Seasons / episodes to refresh afterwards.</param>
    void EnqueueFirst(BaseItem? parent, IReadOnlyList<BaseItem> children);
}
