using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.PirateFinDtos;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// PirateFin accounts: emailed temporary passwords for users who forgot theirs, and choosing a new password after
/// signing in with one.
/// </summary>
[Route("PirateFin")]
public class PirateFinAccountController : BaseJellyfinApiController
{
    private readonly ITemporaryPasswords _temporaryPasswords;
    private readonly IUserManager _userManager;
    private readonly INetworkManager _networkManager;
    private readonly ILogger<PirateFinAccountController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinAccountController"/> class.
    /// </summary>
    /// <param name="temporaryPasswords">Instance of the <see cref="ITemporaryPasswords"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="networkManager">Instance of the <see cref="INetworkManager"/> interface.</param>
    /// <param name="logger">The logger.</param>
    public PirateFinAccountController(
        ITemporaryPasswords temporaryPasswords,
        IUserManager userManager,
        INetworkManager networkManager,
        ILogger<PirateFinAccountController> logger)
    {
        _temporaryPasswords = temporaryPasswords;
        _userManager = userManager;
        _networkManager = networkManager;
        _logger = logger;
    }

    /// <summary>
    /// Emails a temporary password to a user who forgot theirs, when they have an email address. Works from anywhere;
    /// users without an email address use Jellyfin's own reset (from the home network). From outside the home network
    /// the answer is always the same (emailed), so it can't be used to find out which user names exist.
    /// </summary>
    /// <param name="request">The user name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Whether an email was sent (always <c>true</c> from outside the home network).</response>
    /// <response code="400">The email couldn't be sent (only answered on the home network).</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="TemporaryPasswordResultDto"/>.</returns>
    [HttpPost("ForgotPassword")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TemporaryPasswordResultDto>> ForgotPassword([FromBody, Required] ForgotPasswordRequestDto request, CancellationToken cancellationToken)
    {
        if (!_networkManager.IsInLocalNetwork(HttpContext.GetNormalizedRemoteIP()))
        {
            // Sent in the background and answered at once, so neither the answer nor how long it takes tells anything.
            var username = request.Username;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await _temporaryPasswords.SendAsync(username, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to email a temporary password");
                    }
                },
                CancellationToken.None);
            return new TemporaryPasswordResultDto { Emailed = true };
        }

        try
        {
            return new TemporaryPasswordResultDto { Emailed = await _temporaryPasswords.SendAsync(request.Username, cancellationToken).ConfigureAwait(false) };
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to email a temporary password");
            return BadRequest("The email with your temporary password couldn't be sent. Please try again later or ask your admin.");
        }
    }

    /// <summary>
    /// Gets whether the signed in user signed in with a temporary password and must choose a new one.
    /// </summary>
    /// <response code="200">Whether a new password is required.</response>
    /// <returns>An <see cref="OkResult"/> containing a value indicating whether a new password is required.</returns>
    [HttpGet("Users/Me/PasswordChangeRequired")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<bool> IsPasswordChangeRequired()
        => _temporaryPasswords.IsChangeRequired(RequestHelpers.GetUserId(User, null));

    /// <summary>
    /// Sets the new password of a user who signed in with a temporary password.
    /// </summary>
    /// <param name="request">The new password.</param>
    /// <response code="204">Changed.</response>
    /// <response code="400">Empty, or the user didn't sign in with a temporary password.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Users/Me/Password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SetNewPassword([FromBody, Required] NewPasswordDto request)
    {
        var userId = RequestHelpers.GetUserId(User, null);
        if (!_temporaryPasswords.IsChangeRequired(userId))
        {
            return BadRequest("Change your password on your profile page.");
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest("Enter a new password.");
        }

        await _userManager.ChangePassword(userId, request.NewPassword).ConfigureAwait(false);
        _temporaryPasswords.ClearChangeRequired(userId);
        return NoContent();
    }
}
