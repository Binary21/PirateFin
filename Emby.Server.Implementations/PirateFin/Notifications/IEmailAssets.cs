using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// What emails show beyond their text: posters, and links to PirateFin's public address.
/// </summary>
public interface IEmailAssets
{
    /// <summary>
    /// Gets the poster an email about the item shows (the series' for an episode or season), shrunk for email.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The JPEG, or <c>null</c> when there is none.</returns>
    Task<byte[]?> GetPosterAsync(BaseItem? item);

    /// <summary>
    /// Gets the link to an item's page.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The link, or <c>null</c> when no public address is set.</returns>
    string? GetItemLink(Guid itemId);

    /// <summary>
    /// Gets a link into the web app, e.g. "dashboard/piratefin/requests".
    /// </summary>
    /// <param name="route">The route after #/.</param>
    /// <returns>The link, or <c>null</c> when no public address is set.</returns>
    string? GetLink(string route);
}
