using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// What a user watches and asks for, for the dashboard's Insights tab. Watch time comes from PirateFin's playback log,
/// which starts when the feature was installed; genres, rewatches, current titles and drop-offs come from the user's
/// played state and include older history.
/// </summary>
public sealed class UserInsights
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the activity summary.
    /// </summary>
    public ActivitySummary Activity { get; set; } = new();

    /// <summary>
    /// Gets or sets the seconds watched per day for the last 90 days, oldest first, in the viewer's time zone.
    /// </summary>
    public IReadOnlyList<DailyWatchTime> Daily { get; set; } = [];

    /// <summary>
    /// Gets or sets the seconds watched in the last 90 days per weekday (0 = Sunday) and hour (0-23), in the viewer's
    /// time zone.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<long>> Heatmap { get; set; } = [];

    /// <summary>
    /// Gets or sets the genre ranking of each library.
    /// </summary>
    public IReadOnlyList<GenreRanking> Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the most watched series and movies of the last 90 days, by watch time.
    /// </summary>
    public IReadOnlyList<TitleWatchTime> TopTitles { get; set; } = [];

    /// <summary>
    /// Gets or sets the movies and episodes played more than once, most plays first.
    /// </summary>
    public IReadOnlyList<InsightTitle> Rewatches { get; set; } = [];

    /// <summary>
    /// Gets or sets the series and movies the user is watching now (played in the last 30 days, not finished).
    /// </summary>
    public IReadOnlyList<InsightTitle> CurrentlyWatching { get; set; } = [];

    /// <summary>
    /// Gets or sets the series and movies the user started and left: no progress in 30 days and not finished.
    /// </summary>
    public IReadOnlyList<InsightTitle> DropOffs { get; set; } = [];

    /// <summary>
    /// Gets or sets the seasons the user is subscribed to, newest first.
    /// </summary>
    public IReadOnlyList<InsightSubscription> Subscriptions { get; set; } = [];

    /// <summary>
    /// Gets or sets the user's download requests, newest first.
    /// </summary>
    public IReadOnlyList<DownloadRequest> Requests { get; set; } = [];

    /// <summary>
    /// Gets or sets the user's searches of the last 90 days that found nothing, newest first.
    /// </summary>
    public IReadOnlyList<SearchRecord> FailedSearches { get; set; } = [];

    /// <summary>
    /// Gets or sets hints for the admin on what this user would like.
    /// </summary>
    public IReadOnlyList<InsightSuggestion> Suggestions { get; set; } = [];
}
