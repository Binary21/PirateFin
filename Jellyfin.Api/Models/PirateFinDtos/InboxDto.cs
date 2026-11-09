using System.Collections.Generic;
using MediaBrowser.Model.PirateFin;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The signed in user's bell notifications.
/// </summary>
public class InboxDto
{
    /// <summary>
    /// Gets or sets the notifications, newest first.
    /// </summary>
    public IReadOnlyList<UserNotification> Notifications { get; set; } = [];

    /// <summary>
    /// Gets or sets the number not seen yet (the bell shows a dot while there are any).
    /// </summary>
    public int Unseen { get; set; }
}
