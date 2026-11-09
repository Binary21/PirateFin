using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using MediaBrowser.Model.PirateFin;

namespace Emby.Server.Implementations.PirateFin.YouTube;

/// <summary>
/// Small rules about YouTube data: video lengths, Shorts, and what a user typed to identify their channel.
/// </summary>
public static partial class YouTubeParsing
{
    /// <summary>
    /// Shorts can be up to 3 minutes; anything this short or shorter is treated as one.
    /// </summary>
    public const int ShortsMaxSeconds = 60;

    /// <summary>
    /// Videos up to this length are treated as Shorts when they say #shorts.
    /// </summary>
    public const int TaggedShortsMaxSeconds = 180;

    /// <summary>
    /// Converts an ISO 8601 duration (e.g. PT1H2M3S) to seconds.
    /// </summary>
    /// <param name="duration">The duration, or <c>null</c>.</param>
    /// <returns>The seconds; 0 when unknown.</returns>
    public static int ParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
        {
            return 0;
        }

        try
        {
            return (int)XmlConvert.ToTimeSpan(duration).TotalSeconds;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a video is a Short (the tab shows regular videos only). The API doesn't say,
    /// so it's judged by length and the #shorts tag.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns><c>true</c> for a Short.</returns>
    public static bool IsShort(YouTubeVideo video)
        => (video.DurationSeconds > 0 && video.DurationSeconds <= ShortsMaxSeconds)
            || (video.DurationSeconds <= TaggedShortsMaxSeconds
                && (video.Title.Contains("#shorts", StringComparison.OrdinalIgnoreCase) || video.Description.Contains("#shorts", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Works out what a user typed to identify their channel: a channel id (UC…), a @handle, or a link to either.
    /// </summary>
    /// <param name="input">What they typed.</param>
    /// <returns>The channel id or the handle (one of them), or neither when it can't be read.</returns>
    public static (string? ChannelId, string? Handle) ParseChannelInput(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        var id = ChannelIdRegex().Match(text);
        if (id.Success)
        {
            return (id.Value, null);
        }

        var handle = HandleRegex().Match(text);
        return handle.Success ? (null, handle.Groups[1].Value) : (null, null);
    }

    /// <summary>
    /// Gets a channel's uploads playlist: its id with UU in place of UC.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <returns>The playlist id.</returns>
    public static string GetUploadsPlaylistId(string channelId)
        => channelId.StartsWith("UC", StringComparison.Ordinal) ? "UU" + channelId[2..] : channelId;

    /// <summary>
    /// Finds the channel a search names: its name or @handle equals the search, ignoring case, spaces and punctuation.
    /// </summary>
    /// <param name="channels">The channels to look in.</param>
    /// <param name="query">The search text.</param>
    /// <returns>The channel, or <c>null</c>.</returns>
    public static YouTubeChannel? MatchChannel(IEnumerable<YouTubeChannel> channels, string query)
    {
        var wanted = Normalize(query);
        if (wanted.Length == 0)
        {
            return null;
        }

        return channels.FirstOrDefault(c => Normalize(c.Title) == wanted || Normalize(c.Handle) == wanted);
    }

    /// <summary>
    /// Turns a search into the @handle it could be (letters, digits, dots, dashes and underscores; 3 to 30 long).
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <returns>The handle without @, or <c>null</c> when it can't be one.</returns>
    public static string? ToHandle(string query)
    {
        var handle = string.Concat(query.Trim().TrimStart('@').Where(c => !char.IsWhiteSpace(c)));
        return HandleOnlyRegex().IsMatch(handle) ? handle : null;
    }

    private static string Normalize(string? text)
        => string.Concat((text ?? string.Empty).TrimStart('@').Where(char.IsLetterOrDigit)).ToUpperInvariant();

    [GeneratedRegex(@"^[\w.\-]{3,30}$")]
    private static partial Regex HandleOnlyRegex();

    [GeneratedRegex(@"UC[\w-]{22}")]
    private static partial Regex ChannelIdRegex();

    [GeneratedRegex(@"@([\w.\-]{3,30})")]
    private static partial Regex HandleRegex();
}
