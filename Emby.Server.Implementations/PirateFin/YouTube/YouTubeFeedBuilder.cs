using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.PirateFin;

namespace Emby.Server.Implementations.PirateFin.YouTube;

/// <summary>
/// Orders the YouTube home grid: recent uploads from subscriptions, mixed with recent videos from similar creators,
/// then older subscription videos not watched (or not finished), then older similar videos not watched. Shorts and live
/// streams are left out; kids profiles only get videos made for kids.
/// </summary>
public static class YouTubeFeedBuilder
{
    /// <summary>
    /// Videos published within this time are "recent".
    /// </summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(7);

    /// <summary>
    /// While both are available, this many subscription videos come before each recommended one.
    /// </summary>
    public const int SubscribedPerRecommended = 3;

    /// <summary>
    /// Builds the grid.
    /// </summary>
    /// <param name="videos">The candidate videos (subscriptions and recommendations), with the user's watch state.</param>
    /// <param name="utcNow">The current time.</param>
    /// <param name="kidsOnly">Whether only videos made for kids may show.</param>
    /// <returns>The videos in grid order.</returns>
    public static List<YouTubeVideo> Build(IEnumerable<YouTubeVideo> videos, DateTime utcNow, bool kidsOnly)
    {
        var shown = videos
            .Where(v => !v.IsLive && !YouTubeParsing.IsShort(v) && (!kidsOnly || v.MadeForKids) && !v.Watched)
            .DistinctBy(v => v.Id)
            .ToList();

        bool IsRecent(YouTubeVideo v) => utcNow - v.PublishedUtc <= Recent;
        List<YouTubeVideo> Group(bool subscribed, bool recent) => shown
            .Where(v => v.FromSubscription == subscribed && IsRecent(v) == recent)
            .OrderByDescending(v => v.PublishedUtc)
            .ToList();

        var feed = Interleave(Group(true, true), Group(false, true));
        feed.AddRange(Group(true, false));
        feed.AddRange(Group(false, false));
        return feed;
    }

    private static List<YouTubeVideo> Interleave(List<YouTubeVideo> subscribed, List<YouTubeVideo> recommended)
    {
        var result = new List<YouTubeVideo>(subscribed.Count + recommended.Count);
        int s = 0, r = 0;
        while (s < subscribed.Count || r < recommended.Count)
        {
            for (var i = 0; i < SubscribedPerRecommended && s < subscribed.Count; i++)
            {
                result.Add(subscribed[s++]);
            }

            if (r < recommended.Count)
            {
                result.Add(recommended[r++]);
            }
        }

        return result;
    }
}
