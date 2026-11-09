using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Trakt API client for the TV calendar and show search.
/// </summary>
public sealed class TraktClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<InternetProviderOptions> _options;
    private readonly ILogger<TraktClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public TraktClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<InternetProviderOptions> options, ILogger<TraktClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gets the episodes airing in a date range, filtered by the configured language / country / network / genre rules.
    /// </summary>
    /// <param name="startDateUtc">The first day.</param>
    /// <param name="days">The number of days.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episodes.</returns>
    public async Task<IReadOnlyList<ScheduleEpisode>> GetCalendarAsync(DateTime startDateUtc, int days, CancellationToken cancellationToken)
    {
        var start = startDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var doc = await GetJsonAsync($"/calendars/all/shows/{start}/{days}?extended=full,images", cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return [];
        }

        var options = _options.CurrentValue;
        var episodes = new List<ScheduleEpisode>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("show", out var show) || !item.TryGetProperty("episode", out var episode))
            {
                continue;
            }

            if (!IsAllowedShow(show, options))
            {
                continue;
            }

            // first_aired is the real air time in UTC; updated_at (used previously) is when Trakt last edited the record.
            var firstAired = item.GetUtcDateTime("first_aired") ?? episode.GetUtcDateTime("first_aired");
            if (firstAired is null)
            {
                continue;
            }

            episodes.Add(new ScheduleEpisode
            {
                Title = show.GetString("title"),
                AlternateTitle = show.GetString("ids", "slug"),
                Season = episode.GetInt("season") ?? 1,
                Number = episode.GetInt("number"),
                EpisodeTitle = episode.GetString("title"),
                Overview = episode.GetString("overview"),
                FirstAiring = firstAired,
                ImageUrl = GetImage(show, "poster"),
                Genres = show.GetStringArray("genres")
            });
        }

        return episodes;
    }

    /// <summary>
    /// Searches shows. Seasons are not included.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="limit">The maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The shows.</returns>
    public async Task<IReadOnlyList<SearchedSeries>> SearchShowsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var path = $"/search/show?query={Uri.EscapeDataString(query)}&limit={limit}&extended=full,images";
        using var doc = await GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return [];
        }

        var results = new List<SearchedSeries>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("show", out var show))
            {
                continue;
            }

            var slug = show.GetString("ids", "slug");
            if (string.IsNullOrEmpty(slug))
            {
                continue;
            }

            results.Add(new SearchedSeries
            {
                Title = show.GetString("title"),
                AlternateTitle = slug,
                Year = show.GetInt("year"),
                Overview = show.GetString("overview"),
                ImageUrl = GetImage(show, "poster"),
                Genres = show.GetStringArray("genres")
            });
        }

        return results;
    }

    /// <summary>
    /// Finds a show by its TMDB id, if it passes the configured language / country / network / genre rules (like the
    /// shows PirateFin picks up by itself). Seasons are not included.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The show; <c>null</c> when Trakt doesn't know it or it's filtered out.</returns>
    public async Task<SearchedSeries?> GetAllowedShowByTmdbIdAsync(int tmdbId, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/search/tmdb/{tmdbId}?type=show&extended=full,images"),
            cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("show", out var show) || !IsAllowedShow(show, _options.CurrentValue))
            {
                continue;
            }

            var slug = show.GetString("ids", "slug");
            if (!string.IsNullOrEmpty(slug))
            {
                return new SearchedSeries
                {
                    Title = show.GetString("title"),
                    AlternateTitle = slug,
                    Year = show.GetInt("year"),
                    Overview = show.GetString("overview"),
                    ImageUrl = GetImage(show, "poster"),
                    Genres = show.GetStringArray("genres")
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all seasons (except specials) with their episodes.
    /// </summary>
    /// <param name="slug">The show slug.</param>
    /// <param name="genres">The genres to copy onto the seasons.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The seasons.</returns>
    public async Task<List<SearchedSeason>> GetSeasonsAsync(string slug, string[] genres, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"/shows/{Uri.EscapeDataString(slug)}/seasons?extended=full,episodes", cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return [];
        }

        var seasons = new List<SearchedSeason>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var number = item.GetInt("number") ?? 0;
            if (number == 0)
            {
                // Specials.
                continue;
            }

            var season = new SearchedSeason
            {
                Season = number,
                Title = "Season " + number.ToString(CultureInfo.InvariantCulture),
                Overview = item.GetString("overview"),
                Genres = genres
            };

            if (item.TryGetArray("episodes", out var episodes))
            {
                foreach (var episode in episodes.EnumerateArray())
                {
                    season.Episodes.Add(new SearchedEpisode
                    {
                        Episode = episode.GetInt("number") ?? 0,
                        Title = episode.GetString("title"),
                        Overview = episode.GetString("overview"),
                        FirstAiring = episode.GetUtcDateTime("first_aired")
                    });
                }
            }

            seasons.Add(season);
        }

        return seasons;
    }

    private static bool IsAllowedShow(JsonElement show, InternetProviderOptions options)
    {
        if (!string.Equals(show.GetString("language"), "en", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(show.GetString("country"), "us", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!options.AllowedShowNetworks.Contains(show.GetString("network"), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var genres = show.GetStringArray("genres");
        return !genres.Any(g => options.BannedShowGenres.Contains(g, StringComparer.OrdinalIgnoreCase))
            && genres.Any(g => options.AllowedShowGenres.Contains(g, StringComparer.OrdinalIgnoreCase));
    }

    // Trakt returns image urls without a scheme, e.g. "walter-r2.trakt.tv/images/...".
    private static string GetImage(JsonElement show, string type)
    {
        if (!show.TryGetProperty("images", out var images) || !images.TryGetArray(type, out var urls) || urls.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var url = urls[0].GetString() ?? string.Empty;
        return url.Length == 0 || url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
    }

    private async Task<JsonDocument?> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var doc = await SendAsync(pathAndQuery, cancellationToken).ConfigureAwait(false);
        if (doc is null && pathAndQuery.Contains(",images", StringComparison.Ordinal))
        {
            // Older Trakt deployments reject the images extension; posters are optional so retry without it.
            doc = await SendAsync(pathAndQuery.Replace(",images", string.Empty, StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        }

        return doc;
    }

    private async Task<JsonDocument?> SendAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        using var request = new HttpRequestMessage(HttpMethod.Get, options.TraktApiUrl.TrimEnd('/') + pathAndQuery);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", options.TraktApiKey);
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", options.TraktApiUser);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Trakt request {Path} failed with {StatusCode}", pathAndQuery, response.StatusCode);
            return null;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
