using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Providers.Plugins.Tmdb;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// TMDB's popularity ranking for the Popular rows, with Jellyfin's built-in TMDB key.
/// </summary>
public sealed class TmdbPopularClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbPopularClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbPopularClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="logger">The logger.</param>
    public TmdbPopularClient(IHttpClientFactory httpClientFactory, ILogger<TmdbPopularClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets a page of the most popular shows with episodes airing between two dates (new shows and new seasons).
    /// </summary>
    /// <param name="fromUtc">The first day.</param>
    /// <param name="toUtc">The last day.</param>
    /// <param name="page">The page, from 1 (20 shows each).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TMDB ids, most popular first.</returns>
    public Task<IReadOnlyList<int>> GetPopularShowsAsync(DateTime fromUtc, DateTime toUtc, int page, CancellationToken cancellationToken)
        => GetIdsAsync(
            string.Create(CultureInfo.InvariantCulture, $"/discover/tv?sort_by=popularity.desc&air_date.gte={Day(fromUtc)}&air_date.lte={Day(toUtc)}&page={page}"),
            cancellationToken);

    /// <summary>
    /// Gets a page of the most popular movies released between two dates.
    /// </summary>
    /// <param name="fromUtc">The first day.</param>
    /// <param name="toUtc">The last day.</param>
    /// <param name="page">The page, from 1 (20 movies each).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The TMDB ids, most popular first.</returns>
    public Task<IReadOnlyList<int>> GetPopularMoviesAsync(DateTime fromUtc, DateTime toUtc, int page, CancellationToken cancellationToken)
        => GetIdsAsync(
            string.Create(CultureInfo.InvariantCulture, $"/discover/movie?sort_by=popularity.desc&primary_release_date.gte={Day(fromUtc)}&primary_release_date.lte={Day(toUtc)}&page={page}"),
            cancellationToken);

    /// <summary>
    /// Gets a movie's IMDb id (how YTS finds it).
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The IMDb id, e.g. tt1234567; <c>null</c> when unknown.</returns>
    public async Task<string?> GetMovieImdbIdAsync(int tmdbId, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(string.Create(CultureInfo.InvariantCulture, $"/movie/{tmdbId}/external_ids"), cancellationToken).ConfigureAwait(false);
        var id = doc?.RootElement.GetString("imdb_id");
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<IReadOnlyList<int>> GetIdsAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(pathAndQuery, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetArray("results", out var results))
        {
            return [];
        }

        var ids = new List<int>();
        foreach (var result in results.EnumerateArray())
        {
            if (result.GetInt("id") is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<JsonDocument?> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{BaseUrl}{pathAndQuery}{separator}api_key={TmdbUtils.ApiKey}";
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
