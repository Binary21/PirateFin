using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// YTS client for this year's movies.
/// </summary>
public sealed class YtsClient
{
    private const int PageSize = 50;

    // Safety net: YTS reports its whole catalogue as movie_count, so stop after this many pages even if no older movie shows up.
    private const int MaxPages = 20;

    private static readonly string[] _trackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://open.stealth.si:80/announce",
        "udp://open.demonii.com:1337/announce",
        "https://tracker.moeblog.cn:443/announce",
        "udp://open.dstud.io:6969/announce",
        "udp://tracker.srv00.com:6969/announce",
        "https://tracker.zhuqiy.com:443/announce",
        "https://tracker.pmman.tech:443/announce"
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<InternetProviderOptions> _options;
    private readonly ILogger<YtsClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YtsClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public YtsClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<InternetProviderOptions> options, ILogger<YtsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gets this year's movies that pass the language / rating filters.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movies.</returns>
    public async Task<IReadOnlyList<ScheduleMovie>> GetCurrentYearMoviesAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var currentYear = DateTime.UtcNow.Year;
        var movies = new List<ScheduleMovie>();
        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        // Sorted by year descending, so paging stops at the first movie from an earlier year.
        for (var page = 1; page <= MaxPages; page++)
        {
            var url = $"{options.YtsApiUrl.TrimEnd('/')}/api/v2/list_movies.json?limit={PageSize}&page={page}&sort_by=year&order_by=desc";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.GetString("status") != "ok")
            {
                throw new InvalidOperationException("YTS returned an error. The YtsApiUrl in providers.json may be out of date.");
            }

            if (!root.TryGetProperty("data", out var data) || !data.TryGetArray("movies", out var movieElements) || movieElements.GetArrayLength() == 0)
            {
                break;
            }

            var reachedOlderYear = false;
            foreach (var movie in movieElements.EnumerateArray())
            {
                var year = movie.GetInt("year") ?? 0;
                if (year < currentYear)
                {
                    reachedOlderYear = true;
                    break;
                }

                if (year == currentYear && PassesFilters(movie, options))
                {
                    movies.Add(ToScheduleMovie(movie));
                }
            }

            if (reachedOlderYear)
            {
                break;
            }
        }

        _logger.LogInformation("Fetched {Count} movies from YTS", movies.Count);
        return movies;
    }

    /// <summary>
    /// Searches YTS by title. Applies the same language / rating filters as the latest movies, but any year.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movies, most downloaded first.</returns>
    public async Task<IReadOnlyList<ScheduleMovie>> SearchMoviesAsync(string query, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var url = $"{options.YtsApiUrl.TrimEnd('/')}/api/v2/list_movies.json?limit={PageSize}&query_term={Uri.EscapeDataString(query)}&sort_by=download_count&order_by=desc";
        using var response = await _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.GetString("status") != "ok")
        {
            throw new InvalidOperationException("YTS returned an error. The YtsApiUrl in providers.json may be out of date.");
        }

        if (!root.TryGetProperty("data", out var data) || !data.TryGetArray("movies", out var movieElements))
        {
            return [];
        }

        return movieElements.EnumerateArray()
            .Where(movie => PassesFilters(movie, options))
            .Select(ToScheduleMovie)
            .ToList();
    }

    private static bool PassesFilters(JsonElement movie, InternetProviderOptions options)
        => options.AllowedLanguages.Contains(movie.GetString("language"), StringComparer.OrdinalIgnoreCase)
            && movie.GetDouble("rating") >= options.MovieMinimumRating;

    private static ScheduleMovie ToScheduleMovie(JsonElement movie) => new()
    {
        Title = movie.GetString("title"),
        Year = movie.GetInt("year") ?? 0,
        ImdbId = movie.GetString("imdb_code"),
        Overview = movie.GetString("summary"),
        FirstAiring = movie.GetUnixUtcDateTime("date_uploaded_unix"),
        ImageUrl = FirstNonEmpty(movie.GetString("large_cover_image"), movie.GetString("medium_cover_image"), movie.GetString("small_cover_image")),
        Genres = movie.GetStringArray("genres"),
        MagnetUrl = GetMagnet(movie)
    };

    private static async Task<JsonDocument> ParseAsync(System.IO.Stream stream, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    // Prefers 1080p, then 720p.
    private static string GetMagnet(JsonElement movie)
    {
        if (!movie.TryGetArray("torrents", out var torrents))
        {
            return string.Empty;
        }

        var torrent = torrents.EnumerateArray().FirstOrDefault(t => t.GetString("quality") == "1080p");
        if (torrent.ValueKind == JsonValueKind.Undefined)
        {
            torrent = torrents.EnumerateArray().FirstOrDefault(t => t.GetString("quality") == "720p");
        }

        if (torrent.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        // The torrent url ends in the info hash (".../torrent/download/<HASH>"); use it when "hash" is missing.
        var hash = torrent.GetString("hash");
        if (string.IsNullOrWhiteSpace(hash))
        {
            var lastSegment = torrent.GetString("url").TrimEnd('/').Split('/')[^1];
            hash = lastSegment.Length == 40 && lastSegment.All(Uri.IsHexDigit) ? lastSegment : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }

        var magnet = new StringBuilder("magnet:?xt=urn:btih:").Append(hash).Append("&dn=").Append(Uri.EscapeDataString(movie.GetString("title")));
        foreach (var tracker in _trackers)
        {
            magnet.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }

        return magnet.ToString();
    }
}
