using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Users' requests for an admin to find episodes PirateFin couldn't. Open requests count against the user's limit
/// until an admin closes them or the user cancels them.
/// </summary>
public interface IDownloadRequests
{
    /// <summary>
    /// Gets or sets the limit users get unless an admin sets their own; -1 for no limit.
    /// </summary>
    int DefaultLimit { get; set; }

    /// <summary>
    /// Requests an episode PirateFin found no release for, or a season's episodes without one, and emails the admins.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The episode or season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request.</returns>
    /// <exception cref="InvalidOperationException">Nothing to request, already requested, or the limit is reached; the message says which.</exception>
    Task<DownloadRequest> CreateAsync(User user, BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// Gets requests, newest first.
    /// </summary>
    /// <param name="includeClosed">Whether to include closed and cancelled ones.</param>
    /// <returns>The requests.</returns>
    IReadOnlyList<DownloadRequest> GetAll(bool includeClosed);

    /// <summary>
    /// Gets a user's open requests and the ones closed in the last 30 days, newest first.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The requests.</returns>
    IReadOnlyList<DownloadRequest> GetForUser(Guid userId);

    /// <summary>
    /// Gets the number of open requests.
    /// </summary>
    /// <returns>The count.</returns>
    int CountOpen();

    /// <summary>
    /// Gets how many requests a user may make and has open.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The quota.</returns>
    RequestQuota GetQuota(Guid userId);

    /// <summary>
    /// Gets a user's own limit, or <c>null</c> when they get the default.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The limit.</returns>
    int? GetLimit(Guid userId);

    /// <summary>
    /// Sets a user's limit; <c>null</c> to use the default, -1 for no limit.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="limit">The limit.</param>
    void SetLimit(Guid userId, int? limit);

    /// <summary>
    /// Gives a requested episode the release the admin found and starts downloading it.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="episodeId">The episode id.</param>
    /// <param name="magnetUrl">The magnet link.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> when there's no such request or episode.</returns>
    /// <exception cref="ArgumentException">Not a magnet link.</exception>
    Task<bool> SetMagnetAsync(Guid requestId, Guid episodeId, string magnetUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Closes a request (freeing the user's slot) and emails the user.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="status"><see cref="DownloadRequestStatus.Fulfilled"/> or <see cref="DownloadRequestStatus.NotFound"/>.</param>
    /// <param name="note">A note for the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> when there's no such open request.</returns>
    Task<bool> CloseAsync(Guid requestId, DownloadRequestStatus status, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Withdraws a user's own open request.
    /// </summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="userId">The user id.</param>
    /// <returns><c>false</c> when there's no such open request of theirs.</returns>
    bool Cancel(Guid requestId, Guid userId);
}
