using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Source search loop settings, loaded from the "AnimeSubscriptions" configuration section.
/// </summary>
public sealed class SubscriptionOptions
{
    /// <summary>
    /// The name of the configuration section.
    /// </summary>
    public const string SectionName = "AnimeSubscriptions";

    /// <summary>
    /// Gets or sets how often the loop checks for due episodes.
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets how long only the configured authors are searched, starting at the first search attempt.
    /// </summary>
    public TimeSpan AuthorSearchWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets how often authors are searched inside <see cref="AuthorSearchWindow"/>.
    /// </summary>
    public TimeSpan AuthorRetryInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets how often authors are still retried after the fallback sources are offered.
    /// </summary>
    public TimeSpan BackgroundAuthorRetryInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets how long fallback sources are kept before being searched again.
    /// </summary>
    public TimeSpan FallbackRefreshInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Gets or sets how long after airing an episode only gets one fallback search: if nothing turns up by then, nothing
    /// will, and the search stops (an admin can still find it on request).
    /// </summary>
    public TimeSpan SingleFallbackAfterAiring { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets how long after the first search attempt the search gives up.
    /// </summary>
    public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets how long after a language version search (dubs) the next one runs, unless a new sub release of the
    /// season brings it forward. Dubs come out a whole number of weeks after the sub.
    /// </summary>
    public TimeSpan VersionSearchInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets how many weeks after a season ended the dub search keeps going when no dub was ever found.
    /// </summary>
    public int NoDubGiveUpWeeks { get; set; } = 4;

    /// <summary>
    /// Gets or sets the number of fallback sources offered to the user.
    /// </summary>
    public int FallbackCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of episodes searched per pass.
    /// </summary>
    public int PageSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets the number of episodes searched in parallel.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;
}
