using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// AniList GraphQL client for the anime airing schedule and anime search.
/// </summary>
public sealed partial class AniListClient
{
    private const string AiringScheduleQuery = """
        query ($page: Int, $perPage: Int, $airingAtGreater: Int, $airingAtLesser: Int) {
            Page(page: $page, perPage: $perPage) {
                pageInfo { hasNextPage }
                airingSchedules(airingAt_greater: $airingAtGreater, airingAt_lesser: $airingAtLesser, sort: TIME) {
                    airingAt
                    episode
                    media {
                        id
                        title { english romaji native }
                        countryOfOrigin
                        status
                        genres
                        coverImage { large }
                    }
                }
            }
        }
        """;

    private const string SearchQuery = """
        query ($search: String) {
            Page(page: 1, perPage: 25) {
                media(search: $search, type: ANIME, format_in: [TV, ONA]) {
                    id
                    title { romaji english native }
                    description(asHtml: false)
                    format
                    status
                    episodes
                    genres
                    seasonYear
                    countryOfOrigin
                    startDate { year month day }
                    coverImage { large }
                    relations { edges { relationType(version: 2) node { id format type } } }
                    airingSchedule(perPage: 50) { pageInfo { hasNextPage } nodes { episode airingAt } }
                }
            }
        }
        """;

    private const string ScheduleQuery = """
        query ($id: Int, $page: Int) {
            Media(id: $id) {
                airingSchedule(page: $page, perPage: 50) { pageInfo { hasNextPage } nodes { episode airingAt } }
            }
        }
        """;

    private const string PopularQuery = """
        query ($since: FuzzyDateInt, $perPage: Int) {
            Page(page: 1, perPage: $perPage) {
                media(type: ANIME, format_in: [TV, ONA], sort: [POPULARITY_DESC], startDate_greater: $since) {
                    id
                    title { english romaji native }
                    status
                    countryOfOrigin
                }
            }
        }
        """;

    // At most this many pages (50 episodes each) of a show's airing schedule.
    private const int MaxSchedulePages = 40;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<InternetProviderOptions> _options;
    private readonly ILogger<AniListClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniListClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public AniListClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<InternetProviderOptions> options, ILogger<AniListClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gets the episodes airing between two times.
    /// </summary>
    /// <param name="fromUtc">The start of the range.</param>
    /// <param name="toUtc">The end of the range.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episodes.</returns>
    public async Task<IReadOnlyList<ScheduleEpisode>> GetAiringScheduleAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var episodes = new List<ScheduleEpisode>();
        var page = 1;
        bool hasNextPage;
        do
        {
            using var doc = await QueryAsync(
                AiringScheduleQuery,
                new
                {
                    page,
                    perPage = options.AniListPageSize,
                    airingAtGreater = new DateTimeOffset(fromUtc).ToUnixTimeSeconds(),
                    airingAtLesser = new DateTimeOffset(toUtc).ToUnixTimeSeconds()
                },
                cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("Page", out var pageElement))
            {
                break;
            }

            hasNextPage = pageElement.TryGetProperty("pageInfo", out var pageInfo)
                && pageInfo.TryGetProperty("hasNextPage", out var next)
                && next.ValueKind == JsonValueKind.True;

            if (pageElement.TryGetArray("airingSchedules", out var schedules))
            {
                foreach (var schedule in schedules.EnumerateArray())
                {
                    if (!schedule.TryGetProperty("media", out var media)
                        || (options.ExcludedAnimeCountries ?? InternetProviderOptions.DefaultExcludedAnimeCountries).Contains(media.GetString("countryOfOrigin"), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var title = GetPreferredTitle(media);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    episodes.Add(new ScheduleEpisode
                    {
                        Title = title,
                        Season = GetSeasonFromTitle(media),
                        AlternateTitle = GetRomajiTitle(media),
                        Number = schedule.GetInt("episode"),
                        AiringStatus = media.GetString("status"),
                        FirstAiring = schedule.GetUnixUtcDateTime("airingAt"),
                        ImageUrl = media.GetString("coverImage", "large"),
                        Genres = media.GetStringArray("genres")
                    });
                }
            }

            page++;
        }
        while (hasNextPage);

        return episodes;
    }

    /// <summary>
    /// Searches anime and groups the TV and streaming (ONA) entries into series (a prequel / sequel chain becomes the
    /// seasons of one series).
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="allCountries">Whether anime from the excluded countries are found too (for series the user added).</param>
    /// <returns>One <see cref="SearchedSeries"/> per chain, most relevant first; <see cref="SearchedSeries.AniListId"/> is the first season's id.</returns>
    public async Task<IReadOnlyList<SearchedSeries>> SearchSeriesAsync(string query, CancellationToken cancellationToken, bool allCountries = false)
    {
        using var doc = await QueryAsync(SearchQuery, new { search = query }, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("Page", out var page)
            || !page.TryGetArray("media", out var media))
        {
            return [];
        }

        var excluded = allCountries ? [] : _options.CurrentValue.ExcludedAnimeCountries ?? InternetProviderOptions.DefaultExcludedAnimeCountries;
        var entries = media.EnumerateArray()
            .Where(m => m.GetString("format") is "TV" or "ONA"
                && m.GetString("status") != "NOT_YET_RELEASED"
                && !excluded.Contains(m.GetString("countryOfOrigin"), StringComparer.OrdinalIgnoreCase))
            .ToList();

        return BuildSeasonChains(entries).Select(BuildSeries).ToList();
    }

    /// <summary>
    /// Gets the most popular anime (TV and streaming) that started since a date: new shows and new seasons. Anime from
    /// the excluded countries and not yet released ones are left out.
    /// </summary>
    /// <param name="sinceUtc">The earliest start date.</param>
    /// <param name="count">How many to get (at most 50).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Their AniList id, preferred title and romaji title, most popular first.</returns>
    public async Task<IReadOnlyList<(string Id, string Title, string RomajiTitle)>> GetPopularAsync(DateTime sinceUtc, int count, CancellationToken cancellationToken)
    {
        var since = (sinceUtc.Year * 10000) + (sinceUtc.Month * 100) + sinceUtc.Day;
        using var doc = await QueryAsync(PopularQuery, new { since, perPage = Math.Min(count, 50) }, cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("Page", out var page)
            || !page.TryGetArray("media", out var media))
        {
            return [];
        }

        var excluded = _options.CurrentValue.ExcludedAnimeCountries ?? InternetProviderOptions.DefaultExcludedAnimeCountries;
        return media.EnumerateArray()
            .Where(m => m.GetString("status") != "NOT_YET_RELEASED"
                && !excluded.Contains(m.GetString("countryOfOrigin"), StringComparer.OrdinalIgnoreCase))
            .Select(m => (m.GetInt("id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, GetPreferredTitle(m), GetRomajiTitle(m)))
            .Where(m => m.Item1.Length > 0 && !string.IsNullOrWhiteSpace(m.Item2))
            .ToList();
    }

    private static SearchedSeries BuildSeries(List<JsonElement> chain)
    {
        var first = chain[0];
        var series = new SearchedSeries
        {
            Title = GetPreferredTitle(first),
            AlternateTitle = GetRomajiTitle(first),
            Year = first.GetInt("seasonYear"),
            Overview = CleanDescription(first.GetString("description")),
            ImageUrl = first.GetString("coverImage", "large"),
            Genres = first.GetStringArray("genres"),
            AniListId = first.GetInt("id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
        };

        foreach (var entry in chain)
        {
            var number = series.Seasons.Count + 1;
            var season = new SearchedSeason
            {
                Season = number,
                Title = "Season " + number.ToString(CultureInfo.InvariantCulture),
                AlternateTitle = GetPreferredTitle(entry),
                RomajiTitle = GetRomajiTitle(entry),
                Overview = CleanDescription(entry.GetString("description")),
                ImageUrl = entry.GetString("coverImage", "large"),
                Genres = entry.GetStringArray("genres"),
                AniListId = entry.GetInt("id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            };

            // Aired and upcoming episodes; long shows have more pages, fetched by CompleteScheduleAsync when needed.
            if (entry.TryGetProperty("airingSchedule", out var schedule))
            {
                season.HasMoreEpisodes = AddScheduledEpisodes(schedule, season);
            }

            // AniList has no airing schedule for many older shows; fill in from the start date and episode count.
            if (season.Episodes.Count == 0)
            {
                PopulateEpisodesFromStartDate(entry, season);
            }

            series.Seasons.Add(season);
        }

        return series;
    }

    // Groups the search results into prequel -> sequel chains. Spin-offs (entries with an anime PARENT) are dropped.
    private static List<List<JsonElement>> BuildSeasonChains(List<JsonElement> entries)
    {
        var mainEntries = new Dictionary<int, JsonElement>();
        var order = new Dictionary<int, int>();
        foreach (var entry in entries)
        {
            if (entry.GetInt("id") is not { } id || GetRelations(entry).Any(r => r.Type == "PARENT" && r.NodeType == "ANIME"))
            {
                continue;
            }

            mainEntries.TryAdd(id, entry);
            order.TryAdd(id, order.Count);
        }

        JsonElement? Related(JsonElement entry, string relationType)
        {
            foreach (var relation in GetRelations(entry))
            {
                if (relation.Type == relationType && mainEntries.TryGetValue(relation.Id, out var related))
                {
                    return related;
                }
            }

            return null;
        }

        var chains = new List<List<JsonElement>>();
        var used = new HashSet<int>();
        foreach (var root in mainEntries.Values.Where(e => Related(e, "PREQUEL") is null).OrderBy(e => order[e.GetInt("id")!.Value]))
        {
            var chain = new List<JsonElement>();
            JsonElement? current = root;
            while (current is JsonElement entry && used.Add(entry.GetInt("id")!.Value))
            {
                chain.Add(entry);
                current = Related(entry, "SEQUEL");
            }

            if (chain.Count > 0)
            {
                chains.Add(chain);
            }
        }

        return chains;
    }

    /// <summary>
    /// Adds the rest of the airing schedule to the seasons the search returned only part of (shows with more than 50
    /// episodes).
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task CompleteScheduleAsync(SearchedSeries series, CancellationToken cancellationToken)
    {
        foreach (var season in series.Seasons.Where(s => s.HasMoreEpisodes && int.TryParse(s.AniListId, CultureInfo.InvariantCulture, out _)))
        {
            var id = int.Parse(season.AniListId, CultureInfo.InvariantCulture);
            var more = true;
            for (var page = 2; more && page <= MaxSchedulePages; page++)
            {
                using var doc = await QueryAsync(ScheduleQuery, new { id, page }, cancellationToken).ConfigureAwait(false);
                more = doc.RootElement.TryGetProperty("data", out var data)
                    && data.TryGetProperty("Media", out var media)
                    && media.TryGetProperty("airingSchedule", out var schedule)
                    && AddScheduledEpisodes(schedule, season);
            }

            season.HasMoreEpisodes = false;
        }
    }

    // Adds a page of an airing schedule; returns whether there are more pages.
    private static bool AddScheduledEpisodes(JsonElement schedule, SearchedSeason season)
    {
        if (schedule.TryGetArray("nodes", out var nodes))
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var number = node.GetInt("episode") ?? 0;
                if (season.Episodes.All(e => e.Episode != number))
                {
                    season.Episodes.Add(new SearchedEpisode { Episode = number, FirstAiring = node.GetUnixUtcDateTime("airingAt") });
                }
            }
        }

        return schedule.TryGetProperty("pageInfo", out var pageInfo)
            && pageInfo.ValueKind == JsonValueKind.Object
            && pageInfo.TryGetProperty("hasNextPage", out var hasNext)
            && hasNext.ValueKind == JsonValueKind.True;
    }

    private static void PopulateEpisodesFromStartDate(JsonElement entry, SearchedSeason season)
    {
        if (entry.GetString("status") != "FINISHED"
            || entry.GetInt("episodes") is not > 0
            || !entry.TryGetProperty("startDate", out var start)
            || start.GetInt("year") is not { } year)
        {
            return;
        }

        DateTime firstAired;
        try
        {
            firstAired = new DateTime(year, start.GetInt("month") ?? 1, start.GetInt("day") ?? 1, 0, 0, 0, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (firstAired > DateTime.UtcNow)
        {
            return;
        }

        var count = entry.GetInt("episodes")!.Value;
        for (var episode = 1; episode <= count; episode++)
        {
            season.Episodes.Add(new SearchedEpisode { Episode = episode, FirstAiring = firstAired });
        }
    }

    private static IEnumerable<(string Type, int Id, string NodeType)> GetRelations(JsonElement entry)
    {
        if (!entry.TryGetProperty("relations", out var relations) || !relations.TryGetArray("edges", out var edges))
        {
            yield break;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.TryGetProperty("node", out var node) && node.GetInt("id") is { } id)
            {
                yield return (edge.GetString("relationType"), id, node.GetString("type"));
            }
        }
    }

    private static string GetPreferredTitle(JsonElement media)
    {
        var english = media.GetString("title", "english");
        if (!string.IsNullOrWhiteSpace(english))
        {
            return StripSeasonSuffix(english);
        }

        var romaji = media.GetString("title", "romaji");
        return string.IsNullOrWhiteSpace(romaji) ? media.GetString("title", "native") : romaji;
    }

    private static string GetRomajiTitle(JsonElement media) => StripSeasonSuffix(media.GetString("title", "romaji"));

    private static int GetSeasonFromTitle(JsonElement media)
    {
        var match = SeasonSuffixRegex().Match(media.GetString("title", "english"));
        return match.Success && int.TryParse(match.Groups["season"].Value, CultureInfo.InvariantCulture, out var season) ? season : 1;
    }

    private static string StripSeasonSuffix(string title)
    {
        var match = SeasonSuffixRegex().Match(title);
        if (match.Success)
        {
            return title[..match.Index].TrimEnd();
        }

        match = OrdinalSeasonSuffixRegex().Match(title);
        return match.Success ? title[..match.Index].TrimEnd() : title;
    }

    private static string CleanDescription(string description)
        => HtmlTagRegex().Replace(description, string.Empty).Trim();

    private async Task<JsonDocument> QueryAsync(string query, object variables, CancellationToken cancellationToken)
    {
        using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
            .PostAsJsonAsync(_options.CurrentValue.AniListGraphQlUrl, new { query, variables }, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.TryGetArray("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                _logger.LogWarning("AniList returned {Count} GraphQL error(s): {Errors}", errors.GetArrayLength(), errors.GetRawText());
            }

            return doc;
        }
    }

    [GeneratedRegex(@"\s+Season\s+(?<season>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonSuffixRegex();

    [GeneratedRegex(@"\s+\d+(st|nd|rd|th)\s+Season\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSeasonSuffixRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
