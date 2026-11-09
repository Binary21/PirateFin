using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.PirateFin;

namespace Emby.Server.Implementations.PirateFin.Insights;

/// <summary>
/// The calculations behind the Insights tab, on plain data: watch time per day and hour, streaks, top titles and the
/// suggestions for the admin.
/// </summary>
public static class InsightCalculator
{
    /// <summary>
    /// The number of days the watch time charts cover.
    /// </summary>
    public const int Days = 90;

    /// <summary>
    /// A user's genre with fewer titles than this in its library is suggested as worth adding to.
    /// </summary>
    public const int ThinGenreTitles = 15;

    /// <summary>
    /// A request open longer than this many days is pointed out.
    /// </summary>
    public const int WaitingRequestDays = 7;

    /// <summary>
    /// A user who watched nothing for this many days is pointed out.
    /// </summary>
    public const int InactiveDays = 14;

    // A day only counts as active with at least this much watched.
    private const long ActiveDaySeconds = 60;

    /// <summary>
    /// Spreads each session's watched seconds evenly over the time it ran, into local days and hours.
    /// </summary>
    /// <param name="sessions">The sessions.</param>
    /// <param name="offset">The viewer's offset from UTC.</param>
    /// <returns>Seconds per local hour, keyed by the hour's start (local time).</returns>
    public static Dictionary<DateTime, double> SpreadOverHours(IEnumerable<PlaybackSessionRecord> sessions, TimeSpan offset)
    {
        var hours = new Dictionary<DateTime, double>();
        foreach (var session in sessions.Where(s => s.WatchedSeconds > 0))
        {
            var start = session.StartUtc + offset;
            var end = session.EndUtc + offset;
            var span = (end - start).TotalSeconds;
            if (span <= 0)
            {
                var hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Unspecified);
                hours[hour] = hours.GetValueOrDefault(hour) + session.WatchedSeconds;
                continue;
            }

            var cursor = start;
            while (cursor < end)
            {
                var hourStart = new DateTime(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, DateTimeKind.Unspecified);
                var sliceEnd = hourStart.AddHours(1) < end ? hourStart.AddHours(1) : end;
                var share = (sliceEnd - cursor).TotalSeconds / span;
                hours[hourStart] = hours.GetValueOrDefault(hourStart) + (session.WatchedSeconds * share);
                cursor = sliceEnd;
            }
        }

        return hours;
    }

    /// <summary>
    /// Gets the seconds watched per day for the <see cref="Days"/> days up to today, oldest first.
    /// </summary>
    /// <param name="hours">Seconds per local hour.</param>
    /// <param name="today">Today, local.</param>
    /// <returns>The days.</returns>
    public static IReadOnlyList<DailyWatchTime> GetDaily(Dictionary<DateTime, double> hours, DateTime today)
    {
        var perDay = hours.GroupBy(h => h.Key.Date).ToDictionary(g => g.Key, g => g.Sum(h => h.Value));
        return Enumerable.Range(0, Days)
            .Select(i => today.Date.AddDays(i - Days + 1))
            .Select(day => new DailyWatchTime
            {
                Date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Seconds = (long)Math.Round(perDay.GetValueOrDefault(day))
            })
            .ToList();
    }

    /// <summary>
    /// Gets the seconds watched per weekday (0 = Sunday) and hour.
    /// </summary>
    /// <param name="hours">Seconds per local hour.</param>
    /// <returns>7 rows of 24 hours.</returns>
    public static IReadOnlyList<IReadOnlyList<long>> GetHeatmap(Dictionary<DateTime, double> hours)
    {
        var grid = new double[7, 24];
        foreach (var (hour, seconds) in hours)
        {
            grid[(int)hour.DayOfWeek, hour.Hour] += seconds;
        }

        return Enumerable.Range(0, 7)
            .Select(day => (IReadOnlyList<long>)Enumerable.Range(0, 24).Select(hour => (long)Math.Round(grid[day, hour])).ToList())
            .ToList();
    }

    /// <summary>
    /// Counts the days in a row with something watched, ending today, or yesterday when nothing was watched today yet.
    /// </summary>
    /// <param name="daily">Seconds per day, oldest first, ending today.</param>
    /// <returns>The streak in days.</returns>
    public static int GetStreak(IReadOnlyList<DailyWatchTime> daily)
    {
        var last = daily.Count - 1;
        if (last >= 0 && daily[last].Seconds < ActiveDaySeconds)
        {
            last--;
        }

        var streak = 0;
        for (var i = last; i >= 0 && daily[i].Seconds >= ActiveDaySeconds; i--)
        {
            streak++;
        }

        return streak;
    }

    /// <summary>
    /// Counts the days among the last <paramref name="days"/> with something watched.
    /// </summary>
    /// <param name="daily">Seconds per day, oldest first, ending today.</param>
    /// <param name="days">The number of days to look back.</param>
    /// <returns>The active days.</returns>
    public static int GetActiveDays(IReadOnlyList<DailyWatchTime> daily, int days)
        => daily.TakeLast(days).Count(d => d.Seconds >= ActiveDaySeconds);

    /// <summary>
    /// Groups sessions per series or movie, most watched first.
    /// </summary>
    /// <param name="sessions">The sessions.</param>
    /// <param name="limit">The maximum number of titles.</param>
    /// <returns>The titles.</returns>
    public static IReadOnlyList<TitleWatchTime> GetTopTitles(IEnumerable<PlaybackSessionRecord> sessions, int limit)
        => sessions
            .GroupBy(s => s.SeriesId ?? s.ItemId)
            .Select(g => new TitleWatchTime
            {
                ItemId = g.Key,
                Name = g.First().SeriesId.HasValue ? g.First().SeriesName ?? g.First().ItemName ?? string.Empty : g.First().ItemName ?? string.Empty,
                Type = g.First().SeriesId.HasValue ? "Series" : "Movie",
                Library = g.First().Library,
                Seconds = g.Sum(s => s.WatchedSeconds),
                Episodes = g.First().SeriesId.HasValue ? g.Select(s => s.ItemId).Distinct().Count() : 0,
                LastWatchedUtc = g.Max(s => s.EndUtc)
            })
            .Where(t => t.Seconds > 0)
            .OrderByDescending(t => t.Seconds)
            .Take(limit)
            .ToList();

    /// <summary>
    /// Builds the hints for the admin.
    /// </summary>
    /// <param name="genres">The user's genre ranking per library.</param>
    /// <param name="libraryGenreTitles">Titles per genre in each library: library, then genre.</param>
    /// <param name="failedSearches">Searches that found nothing, newest first.</param>
    /// <param name="dropOffs">Titles the user left.</param>
    /// <param name="requests">The user's requests.</param>
    /// <param name="lastWatchedUtc">When the user last watched something; <c>null</c> when never (since tracking).</param>
    /// <param name="utcNow">The current time.</param>
    /// <returns>The suggestions, most useful first.</returns>
    public static IReadOnlyList<InsightSuggestion> GetSuggestions(
        IReadOnlyList<GenreRanking> genres,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> libraryGenreTitles,
        IReadOnlyList<SearchRecord> failedSearches,
        IReadOnlyList<InsightTitle> dropOffs,
        IReadOnlyList<DownloadRequest> requests,
        DateTime? lastWatchedUtc,
        DateTime utcNow)
    {
        var suggestions = new List<InsightSuggestion>();

        // Favourite genres the library has little of.
        foreach (var ranking in genres)
        {
            if (!libraryGenreTitles.TryGetValue(ranking.Library, out var counts))
            {
                continue;
            }

            foreach (var genre in ranking.Genres.Take(3).Where(g => g.Titles >= 2))
            {
                var inLibrary = counts.GetValueOrDefault(genre.Name);
                if (inLibrary < ThinGenreTitles)
                {
                    suggestions.Add(new InsightSuggestion
                    {
                        Kind = "ThinGenre",
                        Text = string.Create(
                            CultureInfo.InvariantCulture,
                            $"Watches a lot of {genre.Name} ({genre.Titles} titles), but {ranking.Library} only has {inLibrary} {genre.Name} titles. Consider adding more.")
                    });
                }
            }
        }

        // Searches that found nothing.
        foreach (var search in failedSearches.DistinctBy(s => (s.Library, s.Query.ToLowerInvariant())).Take(5))
        {
            suggestions.Add(new InsightSuggestion
            {
                Kind = "NothingFound",
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Searched {search.Library} for \"{search.Query}\" on {search.Utc:MMM d} and nothing was found.")
            });
        }

        // Genres they keep leaving.
        foreach (var genre in dropOffs
            .SelectMany(d => d.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Take(3))
        {
            suggestions.Add(new InsightSuggestion
            {
                Kind = "GenreDropOffs",
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Stopped watching {genre.Count()} {genre.Key} titles part way. {genre.Key} may not be a good pick for them.")
            });
        }

        // Requests waiting for an admin.
        foreach (var request in requests.Where(r => r.Status == DownloadRequestStatus.Open && (utcNow - r.CreatedUtc).TotalDays >= WaitingRequestDays))
        {
            suggestions.Add(new InsightSuggestion
            {
                Kind = "WaitingRequest",
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Their request for {request.SeriesName} {request.Title} has been open for {(int)(utcNow - request.CreatedUtc).TotalDays} days.")
            });
        }

        if (lastWatchedUtc.HasValue && (utcNow - lastWatchedUtc.Value).TotalDays >= InactiveDays)
        {
            suggestions.Add(new InsightSuggestion
            {
                Kind = "Inactive",
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Hasn't watched anything in {(int)(utcNow - lastWatchedUtc.Value).TotalDays} days.")
            });
        }

        return suggestions;
    }
}
