using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.PirateFinDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The YouTube tab: linking YouTube, the home grid, watch pages, search, progress, streaming and downloads. Off until
/// an admin turns it on.
/// </summary>
[Route("PirateFin/YouTube")]
[Authorize]
public partial class PirateFinYouTubeController : BaseJellyfinApiController
{
    private readonly IYouTube _youTube;
    private readonly IPirateFinConfigurationStore _configurationStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinYouTubeController"/> class.
    /// </summary>
    /// <param name="youTube">Instance of the <see cref="IYouTube"/> interface.</param>
    /// <param name="configurationStore">Instance of the <see cref="IPirateFinConfigurationStore"/> interface.</param>
    public PirateFinYouTubeController(IYouTube youTube, IPirateFinConfigurationStore configurationStore)
    {
        _youTube = youTube;
        _configurationStore = configurationStore;
    }

    private Guid UserId => RequestHelpers.GetUserId(User, null);

    private bool Enabled => _configurationStore.GetYouTube().Enabled;

    /// <summary>
    /// Gets the YouTube settings (the client secret and API key aren't returned in full).
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeSettingsDto"/>.</returns>
    [HttpGet("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<YouTubeSettingsDto> GetSettings()
    {
        var options = _configurationStore.GetYouTube();
        var (units, searchesLeft) = _youTube.GetQuota();
        return new YouTubeSettingsDto
        {
            Enabled = options.Enabled,
            ApiKey = options.ApiKey,
            ClientId = options.ClientId,
            HasClientSecret = !string.IsNullOrEmpty(options.ClientSecret),
            DailySearchCap = options.DailySearchCap,
            RegionCode = options.RegionCode,
            UnitsUsedToday = units,
            SearchesLeftToday = searchesLeft
        };
    }

    /// <summary>
    /// Saves the YouTube settings; the client secret is encrypted (empty keeps the saved one).
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Saved.</response>
    /// <response code="400">Invalid settings.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveSettings([FromBody, Required] YouTubeSettingsDto settings, CancellationToken cancellationToken)
    {
        var current = _configurationStore.GetYouTube();
        string secret;
        try
        {
            secret = string.IsNullOrWhiteSpace(settings.ClientSecret)
                ? current.ClientSecret
                : _youTube.ProtectClientSecret(settings.ClientSecret.Trim());
        }
        catch (PlatformNotSupportedException ex)
        {
            return BadRequest(ex.Message);
        }

        await _configurationStore.SaveYouTubeAsync(
            new YouTubeOptions
            {
                Enabled = settings.Enabled,
                ApiKey = settings.ApiKey.Trim(),
                ClientId = settings.ClientId.Trim(),
                ClientSecret = secret,
                DailySearchCap = Math.Clamp(settings.DailySearchCap, 0, 100),
                RegionCode = string.IsNullOrWhiteSpace(settings.RegionCode) ? "US" : settings.RegionCode.Trim().ToUpperInvariant()
            },
            cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets whether the YouTube tab is on and how the signed in user's YouTube is linked.
    /// </summary>
    /// <response code="200">The status.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeStatus"/>.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<YouTubeStatus> GetStatus() => _youTube.GetStatus(UserId);

    /// <summary>
    /// Starts "Sign in with Google": a code to enter at google.com/device.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The code.</response>
    /// <response code="400">Not set up (the reason).</response>
    /// <response code="404">YouTube is off.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeDeviceCode"/>.</returns>
    [HttpPost("Link/Google")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<YouTubeDeviceCode>> StartGoogleLink(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        try
        {
            return await _youTube.StartGoogleLinkAsync(UserId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Checks whether the user entered the Google code.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">pending, linked, denied or expired.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeLinkStateDto"/>.</returns>
    [HttpPost("Link/Google/Poll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<YouTubeLinkStateDto>> PollGoogleLink(CancellationToken cancellationToken)
    {
        try
        {
            return new YouTubeLinkStateDto { State = await _youTube.PollGoogleLinkAsync(UserId, cancellationToken).ConfigureAwait(false) };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Links the user by their channel id, @handle or link (their subscriptions must be public).
    /// </summary>
    /// <param name="request">The channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Linked.</response>
    /// <response code="400">Not found or private (the reason).</response>
    /// <response code="404">YouTube is off.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Link/Channel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> LinkChannel([FromBody, Required] YouTubeChannelLinkDto request, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        try
        {
            await _youTube.LinkChannelAsync(UserId, request.Channel, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Unlinks the signed in user's YouTube.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Unlinked.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Link")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Unlink(CancellationToken cancellationToken)
    {
        await _youTube.UnlinkAsync(UserId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Fetches the user's subscriptions and new videos now.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Refreshed.</response>
    /// <response code="404">YouTube is off.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Refresh")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Refresh(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        await _youTube.RefreshAsync(UserId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets a page of the user's YouTube home grid.
    /// </summary>
    /// <param name="startIndex">The first video.</param>
    /// <param name="limit">The number of videos.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The page.</response>
    /// <response code="404">YouTube is off.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeFeedPage"/>.</returns>
    [HttpGet("Feed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<YouTubeFeedPage>> GetFeed([FromQuery] int startIndex = 0, [FromQuery] int limit = 24, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        return await _youTube.GetFeedAsync(UserId, startIndex, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a video's watch page.
    /// </summary>
    /// <param name="videoId">The video id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The details.</response>
    /// <response code="404">YouTube is off, or no such video (or not for kids).</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeVideoDetails"/>.</returns>
    [HttpGet("Videos/{videoId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<YouTubeVideoDetails>> GetVideo([FromRoute, Required] string videoId, CancellationToken cancellationToken)
    {
        if (!Enabled || !IsVideoId(videoId))
        {
            return NotFound();
        }

        var details = await _youTube.GetVideoAsync(UserId, videoId, cancellationToken).ConfigureAwait(false);
        return details is null ? NotFound() : details;
    }

    /// <summary>
    /// Gets a channel's page.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The channel.</response>
    /// <response code="404">YouTube is off, or no such channel.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeChannel"/>.</returns>
    [HttpGet("Channels/{channelId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<YouTubeChannel>> GetChannel([FromRoute, Required] string channelId, CancellationToken cancellationToken)
    {
        if (!Enabled || !ChannelIdRegex().IsMatch(channelId))
        {
            return NotFound();
        }

        var channel = await _youTube.GetChannelAsync(UserId, channelId, cancellationToken).ConfigureAwait(false);
        return channel is null ? NotFound() : channel;
    }

    /// <summary>
    /// Gets a page of a channel's videos, newest first.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="pageToken">The page, from the previous page.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The page.</response>
    /// <response code="404">YouTube is off, or not a channel id.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeChannelVideos"/>.</returns>
    [HttpGet("Channels/{channelId}/Videos")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<YouTubeChannelVideos>> GetChannelVideos([FromRoute, Required] string channelId, [FromQuery] string? pageToken, CancellationToken cancellationToken)
    {
        if (!Enabled || !ChannelIdRegex().IsMatch(channelId))
        {
            return NotFound();
        }

        return await _youTube.GetChannelVideosAsync(UserId, channelId, pageToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches YouTube (a limited number of searches per day across all users).
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The results.</response>
    /// <response code="404">YouTube is off.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeSearchResult"/>.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<YouTubeSearchResult>> Search([FromQuery, Required] string query, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        return await _youTube.SearchAsync(UserId, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves how far the user watched a video.
    /// </summary>
    /// <param name="videoId">The video id.</param>
    /// <param name="progress">The position and length.</param>
    /// <response code="204">Saved.</response>
    /// <response code="404">Not a video id.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Videos/{videoId}/Progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SaveProgress([FromRoute, Required] string videoId, [FromBody, Required] YouTubeProgressDto progress)
    {
        if (!IsVideoId(videoId))
        {
            return NotFound();
        }

        _youTube.SaveProgress(UserId, videoId, progress.PositionSeconds, progress.DurationSeconds);
        return NoContent();
    }

    /// <summary>
    /// Starts streaming a video; returns its HLS playlist's address.
    /// </summary>
    /// <param name="videoId">The video id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The stream.</response>
    /// <response code="400">The video can't be played (the reason).</response>
    /// <response code="404">YouTube is off or not a video id.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="YouTubeStreamDto"/>.</returns>
    [HttpPost("Videos/{videoId}/Stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<YouTubeStreamDto>> StartStream([FromRoute, Required] string videoId, CancellationToken cancellationToken)
    {
        if (!Enabled || !IsVideoId(videoId))
        {
            return NotFound();
        }

        try
        {
            var token = await _youTube.StartStreamAsync(UserId, videoId, cancellationToken).ConfigureAwait(false);
            return new YouTubeStreamDto { Url = $"PirateFin/YouTube/Stream/{token}/index.m3u8" };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Serves a stream's playlist or segment. The unguessable token in the address is the permission (players fetch
    /// segments without sign in headers).
    /// </summary>
    /// <param name="token">The stream's token.</param>
    /// <param name="fileName">index.m3u8 or a segment.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The file.</response>
    /// <response code="404">No such stream or file.</response>
    /// <returns>The playlist or segment.</returns>
    [HttpGet("Stream/{token}/{fileName}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetStreamFile([FromRoute, Required] string token, [FromRoute, Required] string fileName, CancellationToken cancellationToken)
    {
        if (!TokenRegex().IsMatch(token))
        {
            return NotFound();
        }

        var path = await _youTube.GetStreamFileAsync(token, fileName, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return NotFound();
        }

        var isPlaylist = fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        Response.Headers.CacheControl = isPlaylist ? "no-cache" : "public, max-age=3600";
        Response.Headers.AccessControlAllowOrigin = "*";
        return PhysicalFile(path, isPlaylist ? "application/vnd.apple.mpegurl" : "video/mp2t", !isPlaylist);
    }

    /// <summary>
    /// Downloads a video for the signed in user (kept until they delete it).
    /// </summary>
    /// <param name="videoId">The video id.</param>
    /// <response code="204">Queued.</response>
    /// <response code="404">YouTube is off or not a video id.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Videos/{videoId}/Download")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Download([FromRoute, Required] string videoId)
    {
        if (!Enabled || !IsVideoId(videoId))
        {
            return NotFound();
        }

        _youTube.QueueDownload(UserId, videoId);
        return NoContent();
    }

    /// <summary>
    /// Deletes the signed in user's download of a video.
    /// </summary>
    /// <param name="videoId">The video id.</param>
    /// <response code="204">Deleted.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Videos/{videoId}/Download")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult DeleteDownload([FromRoute, Required] string videoId)
    {
        _youTube.DeleteDownload(UserId, videoId);
        return NoContent();
    }

    /// <summary>
    /// Gets the signed in user's downloads and whether each is ready.
    /// </summary>
    /// <response code="200">Video id → ready.</response>
    /// <returns>An <see cref="OkResult"/> containing the downloads.</returns>
    [HttpGet("Downloads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyDictionary<string, bool>> GetDownloads() => Ok(_youTube.GetDownloads(UserId));

    private static bool IsVideoId(string videoId) => VideoIdRegex().IsMatch(videoId);

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$")]
    private static partial Regex ChannelIdRegex();

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex TokenRegex();
}
