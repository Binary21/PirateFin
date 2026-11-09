using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
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
/// PirateFin notifications: the Gmail account (admins), each user's address and email preferences, and their bell.
/// </summary>
[Route("PirateFin")]
[Authorize]
public class PirateFinNotificationsController : BaseJellyfinApiController
{
    private readonly IPirateFinConfigurationStore _configurationStore;
    private readonly IEmailSender _emailSender;
    private readonly IUserNotificationSettings _settings;
    private readonly IUserInbox _inbox;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinNotificationsController"/> class.
    /// </summary>
    /// <param name="configurationStore">Instance of the <see cref="IPirateFinConfigurationStore"/> interface.</param>
    /// <param name="emailSender">Instance of the <see cref="IEmailSender"/> interface.</param>
    /// <param name="settings">Instance of the <see cref="IUserNotificationSettings"/> interface.</param>
    /// <param name="inbox">Instance of the <see cref="IUserInbox"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public PirateFinNotificationsController(
        IPirateFinConfigurationStore configurationStore,
        IEmailSender emailSender,
        IUserNotificationSettings settings,
        IUserInbox inbox,
        IUserManager userManager)
    {
        _configurationStore = configurationStore;
        _emailSender = emailSender;
        _settings = settings;
        _inbox = inbox;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the signed in user's bell notifications.
    /// </summary>
    /// <response code="200">The notifications.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="InboxDto"/>.</returns>
    [HttpGet("Inbox")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<InboxDto> GetInbox()
    {
        var notifications = _inbox.Get(RequestHelpers.GetUserId(User, null));
        return new InboxDto { Notifications = notifications, Unseen = notifications.Count(n => !n.Seen) };
    }

    /// <summary>
    /// Marks the signed in user's notifications as seen (they opened the bell), which clears the dot.
    /// </summary>
    /// <response code="204">Marked.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Inbox/Seen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult MarkInboxSeen()
    {
        _inbox.MarkAllSeen(RequestHelpers.GetUserId(User, null));
        return NoContent();
    }

    /// <summary>
    /// Removes one of the signed in user's notifications.
    /// </summary>
    /// <param name="notificationId">The notification id.</param>
    /// <response code="204">Removed.</response>
    /// <response code="404">The user has no such notification.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Inbox/{notificationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteNotification([FromRoute, Required] Guid notificationId)
        => _inbox.Delete(RequestHelpers.GetUserId(User, null), notificationId) ? NoContent() : NotFound();

    /// <summary>
    /// Removes all of the signed in user's notifications.
    /// </summary>
    /// <response code="204">Removed.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Inbox")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearInbox()
    {
        _inbox.Clear(RequestHelpers.GetUserId(User, null));
        return NoContent();
    }

    /// <summary>
    /// Gets the Gmail account emails are sent from (gmail.json); the app password is never returned.
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="GmailSettingsDto"/>.</returns>
    [HttpGet("Configuration/Gmail")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GmailSettingsDto> GetGmailConfiguration()
    {
        var options = _configurationStore.GetGmail();
        var hasAppPassword = !string.IsNullOrEmpty(options.AppPassword);
        return new GmailSettingsDto
        {
            Address = options.Address,
            FromName = options.FromName,
            HasAppPassword = hasAppPassword,
            AppPasswordUnreadable = hasAppPassword && !_configurationStore.IsGmailAppPasswordReadable(),
            PublicAddress = options.PublicAddress
        };
    }

    /// <summary>
    /// Saves the address the buttons in emails link to (gmail.json).
    /// </summary>
    /// <param name="settings">The address; empty for no buttons.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Not an http(s) address.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Configuration/Email/PublicAddress")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveEmailPublicAddress([FromBody, Required] EmailPublicAddressDto settings, CancellationToken cancellationToken)
    {
        var address = settings.PublicAddress.Trim().TrimEnd('/');
        if (address.Length > 0
            && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            return BadRequest("Enter the full address, starting with https://, e.g. https://piratefin.duckdns.org.");
        }

        await _configurationStore.SaveEmailPublicAddressAsync(address, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Saves the Gmail account to gmail.json, encrypting the app password; it applies without a restart.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Invalid settings.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Configuration/Gmail")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveGmailConfiguration([FromBody, Required] SaveGmailSettingsDto settings, CancellationToken cancellationToken)
    {
        var address = settings.Address.Trim();
        var fromName = string.IsNullOrWhiteSpace(settings.FromName) ? "PirateFin" : settings.FromName.Trim();

        // Turning email off forgets the app password too.
        if (address.Length == 0)
        {
            await _configurationStore.SaveGmailAsync(string.Empty, fromName, string.Empty, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }

        if (!MailAddress.TryCreate(address, out _))
        {
            return BadRequest("The Gmail address isn't a valid email address.");
        }

        // Google shows app passwords as four groups of four letters.
        string? appPassword = null;
        if (!string.IsNullOrWhiteSpace(settings.AppPassword))
        {
            appPassword = string.Concat(settings.AppPassword.Where(c => !char.IsWhiteSpace(c)));
            if (appPassword.Length != 16 || !appPassword.All(char.IsAsciiLetter))
            {
                return BadRequest("An app password is 16 letters, e.g. \"abcd efgh ijkl mnop\". Your normal Gmail password won't work.");
            }
        }
        else if (string.IsNullOrEmpty(_configurationStore.GetGmail().AppPassword))
        {
            return BadRequest("Enter the app password.");
        }

        try
        {
            await _configurationStore.SaveGmailAsync(address, fromName, appPassword, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException ex)
        {
            return BadRequest(ex.Message);
        }

        return NoContent();
    }

    /// <summary>
    /// Sends a test email from the saved Gmail account.
    /// </summary>
    /// <param name="request">Where to send it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Sent.</response>
    /// <response code="400">Not set up, an invalid address, or the server refused it (the reason is returned).</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Email/Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SendTestEmail([FromBody, Required] TestEmailDto request, CancellationToken cancellationToken)
    {
        if (!MailAddress.TryCreate(request.To, out _))
        {
            return BadRequest("Enter a valid email address.");
        }

        try
        {
            await _emailSender.SendTestAsync(request.To, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            // The sender's messages already include Gmail's reason.
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets a user's email address and email preferences.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <response code="200">The settings.</response>
    /// <response code="403">Not allowed to see this user's settings.</response>
    /// <response code="404">No such user.</response>
    /// <returns>An <see cref="OkResult"/> containing the settings.</returns>
    [HttpGet("Users/{userId}/Notifications")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<NotificationSettings> GetNotificationSettings([FromRoute, Required] Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(HttpContext.User, user, true))
        {
            return Forbid();
        }

        return _settings.Get(userId);
    }

    /// <summary>
    /// Saves a user's email address and email preferences.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="settings">The settings.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Not a valid email address.</response>
    /// <response code="403">Not allowed to change this user's settings.</response>
    /// <response code="404">No such user.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Users/{userId}/Notifications")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetNotificationSettings([FromRoute, Required] Guid userId, [FromBody, Required] NotificationSettings settings)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(HttpContext.User, user, true))
        {
            return Forbid();
        }

        if (!string.IsNullOrWhiteSpace(settings.Email) && !MailAddress.TryCreate(settings.Email.Trim(), out _))
        {
            return BadRequest("That isn't a valid email address.");
        }

        _settings.Set(userId, settings);
        return NoContent();
    }
}
