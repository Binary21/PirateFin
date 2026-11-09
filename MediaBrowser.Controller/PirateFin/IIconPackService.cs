using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// The profile icon packs. Users can only pick their profile image from these; admins manage the packs.
/// </summary>
public interface IIconPackService
{
    /// <summary>
    /// Gets every pack, in display order.
    /// </summary>
    /// <returns>The packs.</returns>
    IReadOnlyList<IconPack> GetPacks();

    /// <summary>
    /// Gets the path of an icon's image.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="file">The icon's file name.</param>
    /// <returns>The path, or <c>null</c> when there is no such icon.</returns>
    string? GetIconPath(string packId, string file);

    /// <summary>
    /// Gets the icon the user chose (or was given).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The pack id and file name, or <c>null</c> when unknown.</returns>
    (string PackId, string File)? GetUserIcon(Guid userId);

    /// <summary>
    /// Makes an icon the user's profile image.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="packId">The pack id.</param>
    /// <param name="file">The icon's file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> when there is no such icon.</returns>
    Task<bool> SetUserIconAsync(User user, string packId, string file, CancellationToken cancellationToken);

    /// <summary>
    /// Gives a user without a profile image a random default icon.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task AssignDefaultIconIfMissingAsync(User user, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the packs that ship with PirateFin into the data folder on a new install (when it has no icons folder
    /// yet). Packs an admin deleted later are not brought back.
    /// </summary>
    void InstallBundledPacks();

    /// <summary>
    /// Creates an empty pack.
    /// </summary>
    /// <param name="name">The display name; the folder is named after it.</param>
    /// <returns>The pack.</returns>
    IconPack CreatePack(string name);

    /// <summary>
    /// Renames a pack (and its folder).
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="name">The new display name.</param>
    /// <returns>The pack, with its new id; <c>null</c> when there is no such pack.</returns>
    IconPack? RenamePack(string packId, string name);

    /// <summary>
    /// Deletes a pack and its icons. Users keep the image they chose from it.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <returns><c>false</c> when there is no such pack.</returns>
    bool DeletePack(string packId);

    /// <summary>
    /// Adds an icon to a pack.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="title">The icon's display name.</param>
    /// <param name="png">The PNG image.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The icon; <c>null</c> when there is no such pack.</returns>
    Task<ProfileIcon?> AddIconAsync(string packId, string title, Stream png, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an icon from a pack.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="file">The icon's file name.</param>
    /// <returns><c>false</c> when there is no such icon.</returns>
    bool DeleteIcon(string packId, string file);
}
