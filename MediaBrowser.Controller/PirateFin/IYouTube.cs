using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// The YouTube tab: each user's linked YouTube (subscriptions via the official YouTube Data API, no scraping), their
/// home grid of subscribed and recommended videos, search, watch progress, downloads, and streaming a video through
/// yt-dlp and ffmpeg (nothing is stored unless downloaded).
/// </summary>
public interface IYouTube
{
    /// <summary>
    /// Gets whether the tab is on and how the user's YouTube is linked.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The status.</returns>
    YouTubeStatus GetStatus(Guid userId);

    /// <summary>
    /// Starts "Sign in with Google": a code to enter at google.com/device.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The code.</returns>
    /// <exception cref="InvalidOperationException">Google sign in isn't set up, or Google refused.</exception>
    Task<YouTubeDeviceCode> StartGoogleLinkAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether the user entered the code.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>pending, linked, denied or expired.</returns>
    Task<string> PollGoogleLinkAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Links the user by their channel (id, @handle or channel link), whose subscriptions must be public.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="channel">The channel id, @handle or link.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">Not found, subscriptions private, or not set up (the reason).</exception>
    Task LinkChannelAsync(Guid userId, string channel, CancellationToken cancellationToken);

    /// <summary>
    /// Unlinks the user's YouTube and forgets their feed.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task UnlinkAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the user's subscriptions, their new uploads and recommendations from YouTube.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RefreshAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of the user's home grid (refreshing it first when it's stale).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="startIndex">The first video.</param>
    /// <param name="count">The number of videos.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<YouTubeFeedPage> GetFeedAsync(Guid userId, int startIndex, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a video's watch page: the video, its channel and what to watch next.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="videoId">The video id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The details, or <c>null</c> when YouTube doesn't know the video.</returns>
    Task<YouTubeVideoDetails?> GetVideoAsync(Guid userId, string videoId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a channel's page: banner, avatar, name, @handle, counts, description and whether the user subscribes.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel, or <c>null</c> when YouTube doesn't know it.</returns>
    Task<YouTubeChannel?> GetChannelAsync(Guid userId, string channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a page of a channel's uploads, newest first (regular videos only; made for kids only on kids profiles).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="pageToken">The page, from the previous page; <c>null</c> for the first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<YouTubeChannelVideos> GetChannelVideosAsync(Guid userId, string channelId, string? pageToken, CancellationToken cancellationToken);

    /// <summary>
    /// Searches YouTube (a limited number of searches per day across all users).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The results.</returns>
    Task<YouTubeSearchResult> SearchAsync(Guid userId, string query, CancellationToken cancellationToken);

    /// <summary>
    /// Saves how far the user watched a video.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="videoId">The video id.</param>
    /// <param name="positionSeconds">The position.</param>
    /// <param name="durationSeconds">The video's length.</param>
    void SaveProgress(Guid userId, string videoId, double positionSeconds, double durationSeconds);

    /// <summary>
    /// Starts streaming a video (from its download when there is one) as HLS.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="videoId">The video id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream's token: its playlist is Stream/{token}/index.m3u8.</returns>
    /// <exception cref="InvalidOperationException">The video can't be streamed (the reason).</exception>
    Task<string> StartStreamAsync(Guid userId, string videoId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a file of a stream (its playlist or a segment), waiting a little for one being written.
    /// </summary>
    /// <param name="token">The stream's token.</param>
    /// <param name="fileName">index.m3u8 or a segment name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file path, or <c>null</c>.</returns>
    Task<string?> GetStreamFileAsync(string token, string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a video for the user (up to 1080p), kept until they delete it.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="videoId">The video id.</param>
    void QueueDownload(Guid userId, string videoId);

    /// <summary>
    /// Deletes the user's download of a video (the file goes when no one else downloaded it).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="videoId">The video id.</param>
    void DeleteDownload(Guid userId, string videoId);

    /// <summary>
    /// Gets the videos the user downloaded (or is downloading).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The video ids and whether each is ready.</returns>
    IReadOnlyDictionary<string, bool> GetDownloads(Guid userId);

    /// <summary>
    /// Gets the YouTube Data API quota used today (resets at midnight Pacific time) and the searches left.
    /// </summary>
    /// <returns>Units used and searches left.</returns>
    (int UnitsUsed, int SearchesLeft) GetQuota();

    /// <summary>
    /// Encrypts an OAuth client secret for youtube.json.
    /// </summary>
    /// <param name="secret">The secret.</param>
    /// <returns>The encrypted secret.</returns>
    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    string ProtectClientSecret(string secret);
}
