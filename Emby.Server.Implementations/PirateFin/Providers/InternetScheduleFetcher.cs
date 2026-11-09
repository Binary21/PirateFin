using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// Fetches this week's schedule. The week runs Monday to Monday, US Eastern time (when US TV and simulcasts air).
/// </summary>
public sealed class InternetScheduleFetcher : IInternetScheduleFetcher
{
    private static readonly TimeZoneInfo _eastern = GetEasternTimeZone();

    private readonly TraktClient _trakt;
    private readonly AniListClient _aniList;
    private readonly YtsClient _yts;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="InternetScheduleFetcher"/> class.
    /// </summary>
    /// <param name="trakt">The Trakt client.</param>
    /// <param name="aniList">The AniList client.</param>
    /// <param name="yts">The YTS client.</param>
    /// <param name="timeProvider">The time provider.</param>
    public InternetScheduleFetcher(TraktClient trakt, AniListClient aniList, YtsClient yts, TimeProvider timeProvider)
    {
        _trakt = trakt;
        _aniList = aniList;
        _yts = yts;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduleEpisode>> GetAnimeWeeklyScheduleAsync(CancellationToken cancellationToken)
    {
        var (start, end) = GetWeekUtc(_timeProvider.GetUtcNow());
        return _aniList.GetAiringScheduleAsync(start, end, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduleEpisode>> GetTvWeeklyScheduleAsync(CancellationToken cancellationToken)
    {
        var (start, _) = GetWeekUtc(_timeProvider.GetUtcNow());
        return _trakt.GetCalendarAsync(start, 7, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduleMovie>> GetLatestMoviesAsync(CancellationToken cancellationToken)
        => _yts.GetCurrentYearMoviesAsync(cancellationToken);

    /// <summary>
    /// Gets the current Monday-to-Monday week in Eastern time, as UTC instants.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The start and end of the week.</returns>
    public static (DateTime StartUtc, DateTime EndUtc) GetWeekUtc(DateTimeOffset now)
    {
        var easternNow = TimeZoneInfo.ConvertTime(now, _eastern);
        var daysSinceMonday = ((int)easternNow.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var mondayEastern = DateTime.SpecifyKind(easternNow.Date.AddDays(-daysSinceMonday), DateTimeKind.Unspecified);
        return (
            TimeZoneInfo.ConvertTimeToUtc(mondayEastern, _eastern),
            TimeZoneInfo.ConvertTimeToUtc(mondayEastern.AddDays(7), _eastern));
    }

    private static TimeZoneInfo GetEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}
