using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.PirateFinDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// PirateFin profile icons: users pick their profile image from the icon packs, admins manage the packs.
/// </summary>
[Route("PirateFin")]
[Authorize]
public class PirateFinIconsController : BaseJellyfinApiController
{
    private readonly IIconPackService _iconPackService;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinIconsController"/> class.
    /// </summary>
    /// <param name="iconPackService">Instance of the <see cref="IIconPackService"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public PirateFinIconsController(IIconPackService iconPackService, IUserManager userManager)
    {
        _iconPackService = iconPackService;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the icon packs.
    /// </summary>
    /// <response code="200">The packs, in display order.</response>
    /// <returns>An <see cref="OkResult"/> containing the packs.</returns>
    [HttpGet("Icons")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<IconPack>> GetIconPacks() => Ok(_iconPackService.GetPacks());

    /// <summary>
    /// Gets an icon's image.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="file">The icon's file name.</param>
    /// <response code="200">The PNG image.</response>
    /// <response code="404">No such icon.</response>
    /// <returns>The image.</returns>
    [HttpGet("Icons/{packId}/{file}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesImageFile]
    public ActionResult GetIcon([FromRoute, Required] string packId, [FromRoute, Required] string file)
    {
        var path = _iconPackService.GetIconPath(packId, file);
        if (path is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(path, "image/png", true);
    }

    /// <summary>
    /// Gets the icon a user has.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <response code="200">The icon.</response>
    /// <response code="204">The user's image isn't one of the icons (e.g. it was set before icon packs existed).</response>
    /// <returns>The pack id and file name.</returns>
    [HttpGet("Users/{userId}/Icon")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult<SetProfileIconDto> GetUserIcon([FromRoute, Required] Guid userId)
    {
        if (_iconPackService.GetUserIcon(userId) is not var (packId, file))
        {
            return NoContent();
        }

        return new SetProfileIconDto { PackId = packId, File = file };
    }

    /// <summary>
    /// Makes an icon the user's profile image.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="icon">The icon.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Profile image changed.</response>
    /// <response code="403">Not allowed to change this user.</response>
    /// <response code="404">No such user or icon.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Users/{userId}/Icon")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetUserIcon([FromRoute, Required] Guid userId, [FromBody, Required] SetProfileIconDto icon, CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(HttpContext.User, user, true))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to update the image.");
        }

        return await _iconPackService.SetUserIconAsync(user, icon.PackId, icon.File, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : NotFound();
    }

    /// <summary>
    /// Creates an empty icon pack.
    /// </summary>
    /// <param name="request">The pack name.</param>
    /// <response code="200">The pack.</response>
    /// <returns>An <see cref="OkResult"/> containing the pack.</returns>
    [HttpPost("Icons")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IconPack> CreateIconPack([FromBody, Required] IconPackNameDto request)
        => string.IsNullOrWhiteSpace(request.Name) ? BadRequest("The pack needs a name.") : _iconPackService.CreatePack(request.Name);

    /// <summary>
    /// Renames an icon pack.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="request">The new name.</param>
    /// <response code="200">The pack, with its new id.</response>
    /// <response code="404">No such pack.</response>
    /// <returns>An <see cref="OkResult"/> containing the pack.</returns>
    [HttpPost("Icons/{packId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IconPack> RenameIconPack([FromRoute, Required] string packId, [FromBody, Required] IconPackNameDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("The pack needs a name.");
        }

        return _iconPackService.RenamePack(packId, request.Name) is { } pack ? pack : NotFound();
    }

    /// <summary>
    /// Deletes an icon pack and its icons. Users keep the image they chose from it.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">No such pack.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Icons/{packId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteIconPack([FromRoute, Required] string packId)
        => _iconPackService.DeletePack(packId) ? NoContent() : NotFound();

    /// <summary>
    /// Adds an icon to a pack. The request body is the base64 encoded PNG image (the client crops it to a square),
    /// like the body of Jellyfin's own image uploads.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="title">The icon's display name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The icon.</response>
    /// <response code="400">Not a PNG image.</response>
    /// <response code="404">No such pack.</response>
    /// <returns>An <see cref="OkResult"/> containing the icon.</returns>
    [HttpPost("Icons/{packId}/Icons")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileIcon>> AddIcon([FromRoute, Required] string packId, [FromQuery] string? title, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var base64 = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            using var png = new MemoryStream(Convert.FromBase64String(base64));
            var icon = await _iconPackService.AddIconAsync(packId, title ?? string.Empty, png, cancellationToken).ConfigureAwait(false);
            return icon is null ? NotFound() : icon;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Deletes an icon from a pack.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="file">The icon's file name.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">No such icon.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Icons/{packId}/Icons/{file}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteIcon([FromRoute, Required] string packId, [FromRoute, Required] string file)
        => _iconPackService.DeleteIcon(packId, file) ? NoContent() : NotFound();
}
