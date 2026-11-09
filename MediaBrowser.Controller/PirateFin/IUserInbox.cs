using System;
using System.Collections.Generic;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// The notifications in each user's bell: admin replies to their requests and new episodes ready to watch.
/// </summary>
public interface IUserInbox
{
    /// <summary>
    /// Adds a notification to a user's bell.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="kind">What it is about.</param>
    /// <param name="title">The headline.</param>
    /// <param name="text">The details, if any.</param>
    /// <param name="itemId">The item it is about, if any.</param>
    void Add(Guid userId, UserNotificationKind kind, string title, string? text, Guid? itemId);

    /// <summary>
    /// Gets a user's notifications, newest first.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The notifications.</returns>
    IReadOnlyList<UserNotification> Get(Guid userId);

    /// <summary>
    /// Marks all of a user's notifications as seen (they opened the bell).
    /// </summary>
    /// <param name="userId">The user id.</param>
    void MarkAllSeen(Guid userId);

    /// <summary>
    /// Removes one of a user's notifications.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="notificationId">The notification id.</param>
    /// <returns><c>false</c> when the user has no such notification.</returns>
    bool Delete(Guid userId, Guid notificationId);

    /// <summary>
    /// Removes all of a user's notifications.
    /// </summary>
    /// <param name="userId">The user id.</param>
    void Clear(Guid userId);
}
