using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Providers.Plugins.Tmdb;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// Fills in anime episodes' titles, descriptions, stills (and missing air dates) from TMDB. AniList has none of these,
/// and numbers seasons differently from TMDB (a long show is one TMDB season, a split cour two AniList entries), so
/// episodes are matched by air date: the first AniList episode whose date matches a TMDB episode anchors the season,
/// the following episodes are counted on from there and checked against their dates.
/// </summary>
public sealed partial class AnimeEpisodeDetails
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string StillBaseUrl = "https://image.tmdb.org/t/p/original";
    private const int AnimationGenre = 16;
    private const int MaxShowsTried = 4;

    // TMDB returns at most 20 appended seasons per request.
    private const int SeasonsPerRequest = 20;

    private static readonly TimeSpan _cacheLifetime = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (DateTime Created, IReadOnlyList<int> Ids)> _searches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, (DateTime Created, IReadOnlyList<TmdbEpisode> Episodes)> _shows = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeEpisodeDetails> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimeEpisodeDetails"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="logger">The logger.</param>
    public AnimeEpisodeDetails(IHttpClientFactory httpClientFactory, ILogger<AnimeEpisodeDetails> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fills in the episodes of the series' seasons from TMDB, where it has them.
    /// </summary>
    /// <param name="series">The series (an AniList search result).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task FillInAsync(SearchedSeries series, CancellationToken cancellationToken)
    {
        var seasons = series.Seasons.Where(s => s.Episodes.Any(e => e.Episode > 0 && NeedsDetails(e))).ToList();
        if (seasons.Count == 0)
        {
            return;
        }

        try
        {
            var shows = await SearchAsync([series.Title, series.AlternateTitle], cancellationToken).ConfigureAwait(false);
            foreach (var season in seasons)
            {
                var matched = await MatchAsync(season, shows, cancellationToken).ConfigureAwait(false);

                // A later season can be a TMDB show of its own.
                if (matched == 0)
                {
                    var own = await SearchAsync([season.AlternateTitle, season.RomajiTitle], cancellationToken).ConfigureAwait(false);
                    matched = await MatchAsync(season, own.Except(shows).ToList(), cancellationToken).ConfigureAwait(false);
                }

                _logger.LogDebug("TMDB details for {Series} season {Season}: {Matched} episodes", series.Title, season.Season, matched);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not get the episode details of {Series} from TMDB: {Message}", series.Title, ex.Message);
        }
    }

    /// <summary>
    /// Matches a season's episodes to a TMDB show's episodes by air date and fills in what they're missing.
    /// </summary>
    /// <param name="season">The AniList season.</param>
    /// <param name="tmdb">The show's episodes, in order (season, then episode), specials left out.</param>
    /// <returns>How many episodes were matched.</returns>
    internal static int Match(SearchedSeason season, IReadOnlyList<TmdbEpisode> tmdb)
    {
        var episodes = season.Episodes.Where(e => e.Episode > 0).OrderBy(e => e.Episode).ToList();
        SearchedEpisode? anchor = null;
        var anchorIndex = -1;
        foreach (var episode in episodes.Where(e => e.FirstAiring.HasValue))
        {
            anchorIndex = FindByDate(tmdb, episode.FirstAiring!.Value);
            if (anchorIndex >= 0)
            {
                anchor = episode;
                break;
            }
        }

        if (anchor is null)
        {
            return 0;
        }

        // Without a schedule AniList gives every episode the season's start date; then only the count can be trusted.
        var estimatedDates = episodes.Count > 1 && episodes.Select(e => e.FirstAiring).Distinct().Count() == 1;
        var matched = 0;
        foreach (var episode in episodes)
        {
            var index = anchorIndex + (episode.Episode - anchor.Episode);
            var candidate = index >= 0 && index < tmdb.Count ? tmdb[index] : null;

            // Recaps and specials can shift TMDB's numbering; then the date decides.
            if (candidate is not null && !estimatedDates && episode.FirstAiring.HasValue && candidate.AirDate.HasValue
                && Math.Abs((candidate.AirDate.Value - episode.FirstAiring.Value.Date).TotalDays) > 2)
            {
                var byDate = FindByDate(tmdb, episode.FirstAiring.Value);
                candidate = byDate >= 0 ? tmdb[byDate] : null;
            }

            if (candidate is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(episode.Title) && IsRealTitle(candidate.Name))
            {
                episode.Title = candidate.Name;
            }

            if (string.IsNullOrEmpty(episode.Overview))
            {
                episode.Overview = candidate.Overview;
            }

            if (string.IsNullOrEmpty(episode.ImageUrl))
            {
                episode.ImageUrl = candidate.StillUrl;
            }

            if (!episode.FirstAiring.HasValue && candidate.AirDate.HasValue)
            {
                episode.FirstAiring = DateTime.SpecifyKind(candidate.AirDate.Value, DateTimeKind.Utc);
            }

            matched++;
        }

        return matched;
    }

    private static bool NeedsDetails(SearchedEpisode episode)
        => string.IsNullOrEmpty(episode.Title) || string.IsNullOrEmpty(episode.Overview) || !episode.FirstAiring.HasValue;

    // TMDB uses "Episode 5" when nobody has entered a title yet.
    private static bool IsRealTitle(string name)
        => !string.IsNullOrWhiteSpace(name) && !NumberedEpisode().IsMatch(name);

    // The TMDB episode aired on that day (Japanese air times are often the next day in UTC terms: a day either way).
    private static int FindByDate(IReadOnlyList<TmdbEpisode> tmdb, DateTime airedUtc)
    {
        var best = -1;
        var bestDays = double.MaxValue;
        for (var i = 0; i < tmdb.Count; i++)
        {
            if (tmdb[i].AirDate is not { } date)
            {
                continue;
            }

            var days = Math.Abs((date - airedUtc.Date).TotalDays);
            if (days <= 1 && days < bestDays)
            {
                best = i;
                bestDays = days;
            }
        }

        return best;
    }

    [GeneratedRegex(@"^Episode \d+$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedEpisode();

    private async Task<int> MatchAsync(SearchedSeason season, IReadOnlyList<int> shows, CancellationToken cancellationToken)
    {
        foreach (var show in shows.Take(MaxShowsTried))
        {
            var matched = Match(season, await GetEpisodesAsync(show, cancellationToken).ConfigureAwait(false));
            if (matched > 0)
            {
                return matched;
            }
        }

        return 0;
    }

    // Animated TMDB shows found for any of the titles, best match first.
    private async Task<IReadOnlyList<int>> SearchAsync(IEnumerable<string> titles, CancellationToken cancellationToken)
    {
        var ids = new List<int>();
        foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_searches.TryGetValue(title, out var cached) || DateTime.UtcNow - cached.Created >= _cacheLifetime)
            {
                using var doc = await GetAsync("/search/tv?query=" + Uri.EscapeDataString(title), cancellationToken).ConfigureAwait(false);
                var found = new List<int>();
                if (doc is not null && doc.RootElement.TryGetArray("results", out var results))
                {
                    foreach (var result in results.EnumerateArray())
                    {
                        var animated = result.TryGetArray("genre_ids", out var genres)
                            && genres.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.Number && g.GetInt32() == AnimationGenre);
                        if (animated && result.GetInt("id") is { } id)
                        {
                            found.Add(id);
                        }
                    }
                }

                cached = (DateTime.UtcNow, found);
                _searches[title] = cached;
            }

            ids.AddRange(cached.Ids.Where(id => !ids.Contains(id)));
        }

        return ids;
    }

    // Every regular episode of a show, in order (specials left out).
    private async Task<IReadOnlyList<TmdbEpisode>> GetEpisodesAsync(int showId, CancellationToken cancellationToken)
    {
        if (_shows.TryGetValue(showId, out var cached) && DateTime.UtcNow - cached.Created < _cacheLifetime)
        {
            return cached.Episodes;
        }

        var seasonNumbers = new List<int>();
        using (var show = await GetAsync(string.Create(CultureInfo.InvariantCulture, $"/tv/{showId}"), cancellationToken).ConfigureAwait(false))
        {
            if (show is not null && show.RootElement.TryGetArray("seasons", out var seasons))
            {
                seasonNumbers.AddRange(seasons.EnumerateArray()
                    .Select(s => s.GetInt("season_number") ?? 0)
                    .Where(n => n > 0)
                    .Order());
            }
        }

        var episodes = new List<TmdbEpisode>();
        foreach (var batch in seasonNumbers.Chunk(SeasonsPerRequest))
        {
            var append = string.Join(',', batch.Select(n => string.Create(CultureInfo.InvariantCulture, $"season/{n}")));
            using var doc = await GetAsync(string.Create(CultureInfo.InvariantCulture, $"/tv/{showId}?append_to_response={append}"), cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                continue;
            }

            foreach (var number in batch)
            {
                if (!doc.RootElement.TryGetProperty(string.Create(CultureInfo.InvariantCulture, $"season/{number}"), out var season)
                    || !season.TryGetArray("episodes", out var list))
                {
                    continue;
                }

                foreach (var episode in list.EnumerateArray())
                {
                    var airDate = DateTime.TryParseExact(episode.GetString("air_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date
                        : (DateTime?)null;
                    var still = episode.GetString("still_path");
                    episodes.Add(new TmdbEpisode(
                        number,
                        episode.GetInt("episode_number") ?? 0,
                        episode.GetString("name") ?? string.Empty,
                        episode.GetString("overview") ?? string.Empty,
                        airDate,
                        string.IsNullOrEmpty(still) ? string.Empty : StillBaseUrl + still));
                }
            }
        }

        _shows[showId] = (DateTime.UtcNow, episodes);
        return episodes;
    }

    private async Task<JsonDocument?> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{BaseUrl}{pathAndQuery}{separator}language=en-US&api_key={TmdbUtils.ApiKey}";
        using var response = await _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("TMDB request {Path} failed with {StatusCode}", pathAndQuery, response.StatusCode);
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
