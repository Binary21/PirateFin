using System;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Slow downloads: the users who opened an episode or movie whose download turns out slow are told (a popup on its
/// page and an email), and get a bell notification and an email once it can be played.
/// </summary>
public interface ISlowDownloads
{
    /// <summary>
    /// Remembers that a user opened (and so started, or is waiting for) an item's download.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="item">The episode or movie.</param>
    void Register(Guid userId, BaseItem item);
}
