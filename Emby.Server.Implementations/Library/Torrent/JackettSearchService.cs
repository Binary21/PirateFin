using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class JackettSearchService : IDisposable
{
    // TODO: TEMP DELETE LATER
    private static char[] invalidChars = Path.GetInvalidFileNameChars();

    private static readonly Regex EpisodeRegex = new(
    @"\bS(?<season>\d+)[\s._-]*E(?<episode>\d+)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _client;
    private readonly ILogger<JackettSearchService> _logger;
    private readonly JackettOptions _options;

    public JackettSearchService(IOptions<JackettOptions> options, ILogger<JackettSearchService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new HttpClient();
    }

    public async Task<EpisodeDownloadCandidate?> FindBestMatchAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        var season = episode.ParentIndexNumber ?? episode.AiredSeasonNumber ?? 0;
        var number = episode.IndexNumber ?? 0;

        if (episode.LibraryType == "Anime")
        {
            var title = RemoveSpecialCharacters(GetEpisodeTitle(episode));
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var downloadCandidate = await AnimeAuthorSearch(episode, title, season, number, cancellationToken).ConfigureAwait(false);
            if (downloadCandidate != null)
            {
                return downloadCandidate;
            }
        }
        else if (episode.LibraryType == "Shows")
        {
            var title = RemoveSpecialCharacters(GetSeriesTitle(episode));
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var downloadCandidate = await ShowsAuthorSearch(episode, title, season, number, cancellationToken).ConfigureAwait(false);
            if (downloadCandidate != null)
            {
                return downloadCandidate;
            }
        }

        if (IsEpisodeSearchExpired(episode, out var airDateUtc))
        {
            await RemoveEpisodeFromDownloadQueueAsync(episode, airDateUtc, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("No download candidate was found for episode {EpisodeId} in 30 minutes, the episode will be removed from the search queue.", episode.Name);
        }

        return null;
    }

    private async Task<EpisodeDownloadCandidate?> ShowsAuthorSearch(Episode episode, string title, int season, int number, CancellationToken cancellationToken = default)
    {
        foreach (var author in _options.ShowAuthors)
        {
            var query = BuildEpisodeQuery(author, title, season, number);
            var results = await TrySearchAsync(query, limit: 100, cancellationToken, _options.ShowCategory, _options.Indexer["Shows"]).ConfigureAwait(false);
            var best = PickBestResult(results);

            if (best is not null)
            {
                _logger.LogInformation("Found download candidate for {Series} episode {EpisodeId} using author {Author} using search query {Search}", episode.Season.Series.Name, episode.Name, author, query);
                return best;
            }
        }

        _logger.LogInformation("No download candidate was found for episode {EpisodeId} using search queries English: {EngQuery}", episode.Name, BuildEpisodeQuery(string.Empty, title, season, number));
        return null;
    }

    private async Task<EpisodeDownloadCandidate?> AnimeAuthorSearch(Episode episode, string title, int season, int number, CancellationToken cancellationToken = default)
    {
        var seriesTitle = RemoveSpecialCharacters(GetSeriesTitle(episode));
        var romanji = RemoveSpecialCharacters(GetEpisodeRomanji(episode));
        foreach (var author in _options.AnimeAuthors)
        {
            var englishQuery = BuildEpisodeQuery(author, seriesTitle, season, number);
            var englishResults = await TrySearchAsync(englishQuery, limit: 100, cancellationToken, _options.AnimeCategory, _options.Indexer["Anime"]).ConfigureAwait(false);
            var englishBest = PickBestResult(englishResults);

            if (englishBest is not null)
            {
                _logger.LogInformation("Found download candidate for {Series} episode {EpisodeId} using author {Author} using search query {Search}", episode.Season.Series.Name, episode.Name, author, englishQuery);
                return englishBest;
            }

            if (!string.IsNullOrEmpty(seriesTitle) && !string.Equals(seriesTitle, title, StringComparison.OrdinalIgnoreCase))
            {
                var seriesQuery = BuildEpisodeQuery(author, seriesTitle, season, number);
                var seriesResults = await TrySearchAsync(seriesQuery, limit: 100, cancellationToken, _options.AnimeCategory, _options.Indexer["Anime"]).ConfigureAwait(false);
                var seriesBest = PickBestResult(seriesResults);

                if (seriesBest is not null)
                {
                    _logger.LogInformation("Found download candidate for {Series} episode {EpisodeId} using author {Author} using search query {Search}", episode.Season.Series.Name, episode.Name, author, seriesQuery);
                    return seriesBest;
                }
            }

            if (!string.IsNullOrEmpty(romanji))
            {
                var romanjiQuery = BuildEpisodeQuery(author, romanji, season, number);
                var romanjiResults = await TrySearchAsync(romanjiQuery, limit: 100, cancellationToken, _options.AnimeCategory, _options.Indexer["Anime"]).ConfigureAwait(false);
                var romanjiBest = PickBestResult(romanjiResults);

                if (romanjiBest is not null)
                {
                    _logger.LogInformation("Found download candidate for {Series} episode {EpisodeId} using author {Author} using search query {Search}", episode.Season.Series.Name, episode.Name, author, romanjiQuery);
                    return romanjiBest;
                }
            }
        }

        _logger.LogInformation("No download candidate was found for episode {EpisodeId} using search queries English: {EngQuery}, Romanji: {RomQuery}", episode.Name, BuildEpisodeQuery(string.Empty, title, season, number), BuildEpisodeQuery(string.Empty, romanji, season, number));
        return null;
    }

    private static string RemoveSpecialCharacters(string input)
    {
        return Regex.Replace(input, @"[^a-zA-Z0-9 ]", string.Empty);
    }

    public async Task<IReadOnlyList<EpisodeDownloadCandidate>> SearchBulkAsync(string animeTitle, int limit = 100, CancellationToken cancellationToken = default)
    {
        // TODO: fix anime bulk query
        // if (string.IsNullOrWhiteSpace(animeTitle))
        // {
        //     return Array.Empty<EpisodeDownloadCandidate>();
        // }

        // foreach (var author in _options.AnimeAuthors)
        // {
        //     var query = BuildSeasonQuery(author, animeTitle);
        //     var results = await TrySearchAsync(query, limit, cancellationToken, _options.AnimeCategory, _options.Indexer["Anime"]).ConfigureAwait(false);
        //     if (results.Count > 0)
        //     {
        //         return results;
        //     }
        // }

        return Array.Empty<EpisodeDownloadCandidate>();
    }

    public async Task<Dictionary<int, Dictionary<int, EpisodeDownloadCandidate>>> SeachTVShowsBulkAsync(SearchedSeries series, string library, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(series.Title))
        {
            return new Dictionary<int, Dictionary<int, EpisodeDownloadCandidate>>();
        }

        // TODO: TEMP DELETE LATER
        var sanitizedName = new string(series.Title.Where(ch => !invalidChars.Contains(ch)).ToArray());

        Dictionary<int, Dictionary<int, EpisodeDownloadCandidate>> results = [];
        foreach (SearchedSeason season in series.Seasons)
        {
            Dictionary<int, EpisodeDownloadCandidate> seasonEpisodes = [];
            foreach (var author in _options.ShowAuthors)
            {
                var query = BuildSeasonQuery(author, sanitizedName, season.Season);
                UpdateResults(seasonEpisodes, await TrySearchAsync(query, limit, cancellationToken, _options.ShowCategory, _options.Indexer[library]).ConfigureAwait(false));
                if (seasonEpisodes.Count < season.Episodes.Count)
                {
                    continue;
                }

                break;
            }

            results[season.Season] = seasonEpisodes;
        }

        return results;
    }

    private async void UpdateResults(Dictionary<int, EpisodeDownloadCandidate> results, IReadOnlyList<EpisodeDownloadCandidate> newResults)
    {
        foreach (EpisodeDownloadCandidate candidate in newResults)
        {
            results.TryAdd(candidate.Episode, candidate);
        }
    }

    // Swallows per-request failures (bad/disabled indexer, transient network error) so one indexer can't abort
    // the whole search - logs and lets the caller fall through to the next author/indexer instead.
    private async Task<IReadOnlyList<EpisodeDownloadCandidate>> TrySearchAsync(string query, int limit, CancellationToken cancellationToken, string category, string indexer)
    {
        try
        {
            return await SearchAsync(query, limit, cancellationToken, category, indexer).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Search against indexer {Indexer} failed for query '{Query}'", indexer, query);
            return Array.Empty<EpisodeDownloadCandidate>();
        }
    }

    private async Task<IReadOnlyList<EpisodeDownloadCandidate>> SearchAsync(string query, int limit, CancellationToken cancellationToken, string category, string indexer)
    {
        var requestUrl = BuildSearchUrl(query, limit, category, indexer);
        using var response = await _client.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ParseResultsAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private string BuildSearchUrl(string query, int limit, string torrentCategory, string indexer)
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var apiKey = Uri.EscapeDataString(_options.ApiKey ?? string.Empty);
        var search = Uri.EscapeDataString(query);

        var category = Uri.EscapeDataString(torrentCategory);
        var cappedLimit = Math.Clamp(limit, 1, 100);

        return $"{baseUrl}/api/v2.0/indexers/{indexer}/results/torznab/api?apikey={apiKey}&t=search&q={search}&cat={category}&limit={cappedLimit}&cache=false";
    }

    private static string BuildEpisodeQuery(string author, string title, int season, int number)
    {
        var episodeToken = season > 0 && number > 0
            ? $"S{season:00}E{number:00}"
            : number > 0 ? $"E{number:00}" : string.Empty;

        return string.IsNullOrWhiteSpace(episodeToken)
            ? $"{author} {title}".Trim()
            : $"{author} {title} {episodeToken}".Trim();
    }

    private static string BuildSeasonQuery(string author, string title, int season)
    {
        var seasonToken = season > 0 ? $"S{season:00}" : string.Empty;

        return string.IsNullOrWhiteSpace(seasonToken)
            ? $"{author} {title}".Trim()
            : $"{author} {title} {seasonToken}".Trim();
    }

    private static string GetEpisodeTitle(Episode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.Name))
        {
            return episode.Name;
        }

        return episode.SeriesName ?? string.Empty;
    }

    private static string GetSeriesTitle(Episode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.SeriesName))
        {
            return episode.SeriesName;
        }

        return string.Empty;
    }

    private static string GetEpisodeRomanji(Episode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.AlternateTitle))
        {
            return episode.AlternateTitle;
        }

        return string.Empty;
    }

    private static EpisodeDownloadCandidate? PickBestTVShowResult(IReadOnlyList<EpisodeDownloadCandidate> results, string searchQuery)
    {
        var normalizedQuery = NormalizeForSimilarity(searchQuery);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return null;
        }

        return results
            .Where(result => !string.IsNullOrWhiteSpace(result.MagnetUrl))
            .Select(result => new
            {
                Result = result,
                Score = ComputeSimilarityScore(normalizedQuery, result.Title)
            })
            .Where(match => match.Score >= 7)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Result.Seeders)
            .Select(match => match.Result)
            .FirstOrDefault();
    }

    private static int ComputeSimilarityScore(string normalizedQuery, string resultTitle)
    {
        if (string.IsNullOrWhiteSpace(resultTitle))
        {
            return 0;
        }

        var normalizedTitle = NormalizeForSimilarity(resultTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return 0;
        }

        if (!ContainsAllQueryCharacters(normalizedQuery, normalizedTitle))
        {
            return 0;
        }

        if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return 10;
        }

        var maxLength = Math.Max(normalizedQuery.Length, normalizedTitle.Length);
        if (maxLength == 0)
        {
            return 0;
        }

        var distance = ComputeLevenshteinDistance(normalizedQuery, normalizedTitle);
        var similarity = 1d - ((double)distance / maxLength);
        return (int)Math.Round(similarity * 10, MidpointRounding.AwayFromZero);
    }

    private static bool ContainsAllQueryCharacters(string normalizedQuery, string normalizedTitle)
    {
        return normalizedQuery
            .Distinct()
            .All(normalizedTitle.Contains);
    }

    private static string NormalizeForSimilarity(string value)
    {
        return RemoveSpecialCharacters(value)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previousRow = new int[right.Length + 1];
        var currentRow = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previousRow[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            currentRow[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(
                    Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1),
                    previousRow[j - 1] + substitutionCost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[right.Length];
    }

    private static HashSet<string> GetTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = RemoveSpecialCharacters(value).ToLowerInvariant();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static EpisodeDownloadCandidate? PickBestResult(IReadOnlyList<EpisodeDownloadCandidate> results)
        => results
            .Where(result => !string.IsNullOrWhiteSpace(result.MagnetUrl))
            .OrderByDescending(result => result.Seeders)
            .FirstOrDefault();

    // private static async Task<IReadOnlyList<EpisodeDownloadCandidate>> ParseResultsAsync(Stream stream, CancellationToken cancellationToken)
    // {
    //     var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    //     XNamespace torznab = "http://torznab.com/schemas/2015/feed";
//
    //     var results = new List<EpisodeDownloadCandidate>();
//
    //     foreach (var item in document.Descendants("item"))
    //     {
    //         var title = item.Element("title")?.Value?.Trim() ?? string.Empty;
    //         var magnetUrl = GetMagnetUrl(item);
    //         var seeders = GetIntAttribute(item, torznab, "seeders");
//
    //         if (string.IsNullOrWhiteSpace(magnetUrl))
    //         {
    //             continue;
    //         }
//
    //         results.Add(new EpisodeDownloadCandidate(title, magnetUrl, seeders));
    //     }
//
    //     return results;
    // }

    private static async Task<IReadOnlyList<EpisodeDownloadCandidate>> ParseResultsAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";

        // Best candidate per (season, episode), keyed so duplicates collapse as we go.
        var bestByEpisode = new Dictionary<(int Season, int Episode), EpisodeDownloadCandidate>();

        foreach (var item in document.Descendants("item"))
        {
            var title = item.Element("title")?.Value?.Trim() ?? string.Empty;
            var magnetUrl = GetMagnetUrl(item);

            if (string.IsNullOrWhiteSpace(magnetUrl))
            {
                continue;
            }

            var match = EpisodeRegex.Match(title);
            if (!match.Success
                || !int.TryParse(match.Groups["season"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var season)
                || !int.TryParse(match.Groups["episode"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episode))
            {
                continue; // season packs / unparseable titles
            }

            var seeders = GetIntAttribute(item, torznab, "seeders");
            var candidate = new EpisodeDownloadCandidate(title, magnetUrl, seeders, episode);

            var key = (season, episode);
            if (!bestByEpisode.TryGetValue(key, out var existing) || candidate.Seeders > existing.Seeders)
            {
                bestByEpisode[key] = candidate;
            }
        }

        return bestByEpisode.Values
            .OrderBy(c => c.Episode)
            .ToList();
    }

    private static string GetMagnetUrl(XElement item)
    {
        var enclosureUrl = item.Element("enclosure")?.Attribute("url")?.Value;
        if (IsMagnet(enclosureUrl))
        {
            return enclosureUrl!;
        }

        var link = item.Element("link")?.Value;
        if (IsMagnet(link))
        {
            return link!;
        }

        var guid = item.Element("guid")?.Value;
        if (IsMagnet(guid))
        {
            return guid!;
        }

        if (!string.IsNullOrEmpty(enclosureUrl))
        {
            return enclosureUrl!;
        }

        return string.Empty;
    }

    private static bool IsMagnet(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);

    private static int GetIntAttribute(XElement item, XNamespace namespaceName, string attributeName)
    {
        var attribute = item.Descendants(namespaceName + "attr")
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, attributeName, StringComparison.OrdinalIgnoreCase));

        if (attribute is null)
        {
            return 0;
        }

        return int.TryParse(attribute.Attribute("value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static bool IsEpisodeSearchExpired(Episode episode, out DateTime firstAiringUtc)
    {
        firstAiringUtc = default;
        if (!episode.FirstAiring.HasValue)
        {
            return false;
        }

        var firstAiring = episode.FirstAiring.Value;
        firstAiringUtc = firstAiring.Kind == DateTimeKind.Utc
            ? firstAiring
            : firstAiring.ToUniversalTime();

        return DateTime.UtcNow >= firstAiringUtc.AddMinutes(30);
    }

    private async Task RemoveEpisodeFromDownloadQueueAsync(Episode episode, DateTime firstAiringUtc, CancellationToken cancellationToken)
    {
        if (episode.IsDownloadable && !episode.Scheduled)
        {
            return;
        }

        episode.Scheduled = false;
        // Mark as processed so repository candidate scan won't pick this episode again.
        episode.CantDownload = true;
        await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Removed episode {EpisodeId} from download queue because no match was found by 30 minutes after air date {AirDateUtc}",
            episode.Id,
            firstAiringUtc);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
