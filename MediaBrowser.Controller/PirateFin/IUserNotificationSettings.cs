using System;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// The users' email addresses and email preferences.
/// </summary>
public interface IUserNotificationSettings
{
    /// <summary>
    /// Gets a user's settings (an empty address when they haven't entered one).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The settings.</returns>
    NotificationSettings Get(Guid userId);

    /// <summary>
    /// Saves a user's settings.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="settings">The settings.</param>
    void Set(Guid userId, NotificationSettings settings);
}
