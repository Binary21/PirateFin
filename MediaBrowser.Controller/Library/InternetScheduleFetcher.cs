using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Options;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Returns episodes of the week from the internet.
    /// </summary>
    public class InternetScheduleFetcher : IInternetScheduleFetcher
    {
        private static readonly HttpClient _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 50,
            EnableMultipleHttp2Connections = true
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private static readonly TimeZoneInfo _easternTimeZone = GetEasternTimeZone();
        private static readonly Regex _seasonSuffixRegex = new Regex(@"\s+Season\s+(?<season>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _ordinalSeasonSuffixRegex = new Regex(@"\s+\d+(st|nd|rd|th)\s+Season\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string AniListAiringScheduleQuery = @"
            query ($page: Int, $perPage: Int, $airingAtGreater: Int, $airingAtLesser: Int) {
                Page(page: $page, perPage: $perPage) {
                    pageInfo {
                        hasNextPage
                    }
                    airingSchedules(airingAt_greater: $airingAtGreater, airingAt_lesser: $airingAtLesser, sort: TIME) {
                        airingAt
                        episode
                        media {
                            title {
                                english
                                romaji
                                native
                            }
                            countryOfOrigin
                            status
                            genres
                            coverImage {
                                large
                            }
                        }
                    }
                }
            }";

        private static readonly HttpClient _client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 50,
            EnableMultipleHttp2Connections = true
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly InternetProviderOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="InternetScheduleFetcher"/> class.
        /// </summary>
        /// <param name="options">The provider options.</param>
        public InternetScheduleFetcher(IOptions<InternetProviderOptions> options)
        {
            _options = options.Value;
        }

        public async Task<List<ScheduleEpisode>> GetTVShowWeeklySchedule()
        {
            var tvShows = new List<ScheduleEpisode>();
            var nowEastern = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _easternTimeZone);
            var daysSinceWeekStart = ((int)nowEastern.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            var weekStartEastern = nowEastern.Date.AddDays(-daysSinceWeekStart);
            var startDateUtc = TimeZoneInfo.ConvertTimeToUtc(weekStartEastern, _easternTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var url = $"{_options.TraktApiUrl}/calendars/all/shows/{startDateUtc}/7?extended=full";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.TraktApiKey);
            request.Headers.Add("User-Agent", _options.TraktApiUser);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return tvShows;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var item in root.EnumerateArray())
            {
                try
                {
                    var episode = ParseEpisode(item);
                    if (episode != null)
                    {
                        tvShows.Add(episode);
                    }
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
            }

            return tvShows;
        }

        /// <summary>
        /// returns anime episodes of the week.
        /// </summary>
        /// <returns> taks list episode object.</returns>
        public async Task<List<ScheduleEpisode>> GetAnimeWeeklySchedule()
        {
            var animes = new List<ScheduleEpisode>();
            var currentPage = 1;
            var hasNextPage = true;
            var nowEastern = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _easternTimeZone);
            var daysSinceWeekStart = ((int)nowEastern.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            var weekStartEastern = nowEastern.Date.AddDays(-daysSinceWeekStart);
            var weekEndEastern = weekStartEastern.AddDays(7);
            var airingAtGreater = (int)new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(weekStartEastern, _easternTimeZone)).ToUnixTimeSeconds();
            var airingAtLesser = (int)new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(weekEndEastern, _easternTimeZone)).ToUnixTimeSeconds();

            do
            {
                var page = await GetAiringSchedulePageAsync(currentPage, airingAtGreater, airingAtLesser).ConfigureAwait(false);
                animes.AddRange(page.Episodes);
                hasNextPage = page.HasNextPage;
                currentPage++;
            }
            while (hasNextPage);

            return animes;
        }

        public async Task<List<ScheduleMovie>> GetMovieLatestSchedule()
        {
            var movies = new List<ScheduleMovie>();
            var currentYear = DateTime.Now.Year;
            var url = $"{_options.YtsApiUrl}/api/v2/list_movies.json?minimum_rating={_options.MovieMinimumRating}&limit=20&sort_by=date_added&order_by=desc&year={currentYear}";

            using var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (GetStringProperty(root, "status") != "ok")
            {
                throw new InvalidOperationException($"YTS returned an error. The URL may be expired, please update in the dashboard");
            }

            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("movies", out var movieElements))
            {
                return new List<ScheduleMovie>();
            }

            foreach (var movieElement in movieElements.EnumerateArray())
            {
                if (!_options.AllowedLanguages.Contains(GetStringProperty(movieElement, "language")))
                {
                    continue;
                }

                if (GetInt32Property(movieElement, "year") != currentYear)
                {
                    continue;
                }

                ScheduleMovie tempMovie = new ScheduleMovie();

                tempMovie.Title = GetStringProperty(movieElement, "title");
                tempMovie.AlternateTitle = GetStringProperty(movieElement, "title") + " " + GetInt32Property(movieElement, "year");
                tempMovie.Overview = GetStringProperty(movieElement, "summary");
                tempMovie.ImageUrl = GetStringProperty(movieElement, "large_cover_image") ?? GetStringProperty(movieElement, "medium_cover_image") ?? GetStringProperty(movieElement, "small_cover_image");
                tempMovie.Genres = GetGenres(movieElement);

                movies.Add(tempMovie);
            }

            return movies;
        }

        private ScheduleEpisode? ParseEpisode(JsonElement item)
        {
            var show = item.GetProperty("show");

            // Quick rejection filters first
            var language = show.TryGetProperty("language", out var languageElement) &&
                          languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString() : string.Empty;
            if (language != "en")
            {
                return null;
            }

            var country = show.TryGetProperty("country", out var countryElement) &&
                         countryElement.ValueKind == JsonValueKind.String
                ? countryElement.GetString() : string.Empty;
            if (country != "us")
            {
                return null;
            }

            var network = show.TryGetProperty("network", out var networkElement) &&
                         networkElement.ValueKind == JsonValueKind.String
                ? networkElement.GetString() : string.Empty;
            if (!_options.AllowedShowNetworks.Contains(network))
            {
                return null;
            }

            // Genres check
            if (!show.TryGetProperty("genres", out var genresElement) ||
                genresElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var genres = genresElement.EnumerateArray()
                .Select(g => g.GetString()?.Trim().ToLowerInvariant())
                .Where(g => !string.IsNullOrEmpty(g))
                .ToArray();

            if (genres.Any(g => _options.BannedShowGenres.Contains(g)))
            {
                return null;
            }

            if (!genres.Any(g => _options.AllowedShowGenres.Contains(g)))
            {
                return null;
            }

            // Extract data
            var rating = show.TryGetProperty("rating", out var ratingElement) &&
                        ratingElement.ValueKind == JsonValueKind.Number
                ? ratingElement.GetDouble() : 0.0;

            var title = show.TryGetProperty("title", out var titleProp) ?
                       titleProp.GetString() ?? string.Empty : string.Empty;

            var year = show.TryGetProperty("year", out var yearProp) && yearProp.TryGetInt32(out var y) ? y : 0;

            var episode = item.GetProperty("episode");
            var episodeNumber = episode.TryGetProperty("number", out var numberProp) &&
                               numberProp.TryGetInt32(out var n) ? n : 0;

            var seasonNumber = episode.TryGetProperty("season", out var seasonProp) &&
                              seasonProp.TryGetInt32(out var s) ? s : 0;

            var episodeTitle = episode.TryGetProperty("title", out var epTitleProp) ?
                              epTitleProp.GetString() ?? string.Empty : string.Empty;

            var episodeOverview = episode.TryGetProperty("overview", out var overviewProp) ?
                                 overviewProp.GetString() ?? string.Empty : string.Empty;

            var episodeAirDate = episode.TryGetProperty("updated_at", out var updatedAtProp) &&
                                updatedAtProp.ValueKind == JsonValueKind.String &&
                                DateTimeOffset.TryParse(updatedAtProp.GetString(), out var parsed)
                ? parsed.LocalDateTime : new DateTime(2000, 1, 1);
            var episodePoster = episode.TryGetProperty("images", out var imageList)
                && imageList.TryGetProperty("poster", out var poster)
                && poster.ValueKind == JsonValueKind.Array
                && poster.GetArrayLength() > 0
                ? poster[0].GetString() ?? string.Empty
                : string.Empty;
            return new ScheduleEpisode
            {
                Title = title,
                Season = seasonNumber,
                Number = episodeNumber,
                Overview = episodeOverview,
                FirstAiring = episodeAirDate,
                ImageUrl = episodePoster
            };
        }

        private async Task<(List<ScheduleEpisode> Episodes, bool HasNextPage)> GetAiringSchedulePageAsync(int page, int airingAtGreater, int airingAtLesser)
        {
            var requestBody = new
            {
                query = AniListAiringScheduleQuery,
                variables = new
                {
                    page,
                    perPage = _options.AniListPageSize,
                    airingAtGreater,
                    airingAtLesser
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.AniListGraphQlUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };

            using HttpResponseMessage response = await _client.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                throw new InvalidOperationException($"AniList returned {errors.GetArrayLength()} GraphQL error(s).");
            }

            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("Page", out var pageElement))
            {
                return (new List<ScheduleEpisode>(), false);
            }

            var hasNextPage = false;
            if (pageElement.TryGetProperty("pageInfo", out var pageInfo) && pageInfo.TryGetProperty("hasNextPage", out var hasNextPageElement))
            {
                hasNextPage = hasNextPageElement.GetBoolean();
            }

            var episodes = new List<ScheduleEpisode>();
            if (!pageElement.TryGetProperty("airingSchedules", out var airingSchedules) || airingSchedules.ValueKind != JsonValueKind.Array)
            {
                return (episodes, hasNextPage);
            }

            foreach (JsonElement airingSchedule in airingSchedules.EnumerateArray())
            {
                if (!airingSchedule.TryGetProperty("media", out var media))
                {
                    continue;
                }

                // Filter out Chinese-origin anime (donghua). MAKE CONFIGURABLE
                var countryOfOrigin = GetStringProperty(media, "countryOfOrigin");
                if (string.Equals(countryOfOrigin, "CN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = GetPreferredTitle(media);
                var season = GetPreferredTitleSeason(media);
                var romaji = GetRomajiTitle(media);
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                episodes.Add(new ScheduleEpisode
                {
                    Title = title,
                    Season = season,
                    AlternateTitle = romaji,
                    Number = GetInt32Property(airingSchedule, "episode"),
                    AiringStatus = GetStringProperty(media, "status"),
                    FirstAiring = GetUnixTimeProperty(airingSchedule, "airingAt")?.AddHours(-4),
                    ImageUrl = GetStringProperty(media, "coverImage", "large"),
                    Genres = GetGenres(media)
                });
            }

            return (episodes, hasNextPage);
        }

        private static string GetRomajiTitle(JsonElement media)
        {
            if (!media.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var romaji = GetStringProperty(title, "romaji");
            if (string.IsNullOrWhiteSpace(romaji))
            {
                return string.Empty;
            }

            var seasonMatch = _seasonSuffixRegex.Match(romaji);
            if (seasonMatch.Success)
            {
                return romaji.Substring(0, seasonMatch.Index).TrimEnd();
            }

            var ordinalSeasonMatch = _ordinalSeasonSuffixRegex.Match(romaji);
            if (ordinalSeasonMatch.Success)
            {
                return romaji.Substring(0, ordinalSeasonMatch.Index).TrimEnd();
            }

            return romaji;
        }

        private static string[] GetGenres(JsonElement media)
        {
            return media.TryGetProperty("genres", out var genres) &&
                genres.ValueKind == JsonValueKind.Array
            ? genres.EnumerateArray()
                    .Where(g => g.ValueKind == JsonValueKind.String)
                    .Select(g => g.GetString()!)
                    .ToArray()
            : Array.Empty<string>();
        }

        private static string GetPreferredTitle(JsonElement media)
        {
            if (!media.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var english = GetStringProperty(title, "english");
            if (!string.IsNullOrWhiteSpace(english))
            {
                var seasonMatch = _seasonSuffixRegex.Match(english);
                if (seasonMatch.Success)
                {
                    return english.Substring(0, seasonMatch.Index).TrimEnd();
                }

                return english;
            }

            var romaji = GetStringProperty(title, "romaji");
            if (!string.IsNullOrWhiteSpace(romaji))
            {
                return romaji;
            }

            return GetStringProperty(title, "native");
        }

        private static int GetPreferredTitleSeason(JsonElement media)
        {
            if (!media.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.Object)
            {
                return 1;
            }

            var english = GetStringProperty(title, "english");
            if (!string.IsNullOrWhiteSpace(english))
            {
                var seasonMatch = _seasonSuffixRegex.Match(english);
                if (seasonMatch.Success)
                {
                    if (int.TryParse(seasonMatch.Groups["season"].Value, out int result))
                    {
                        return result;
                    }

                    return 1;
                }
            }

            return 1;
        }

        private static string GetStringProperty(JsonElement element, params string[] propertyPath)
        {
            foreach (var propertyName in propertyPath)
            {
                if (!element.TryGetProperty(propertyName, out element))
                {
                    return string.Empty;
                }
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        }

        private static int? GetInt32Property(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var intValue)
                ? intValue
                : null;
        }

        private static DateTime? GetUnixTimeProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value) || !value.TryGetInt32(out var unixTime))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
        }

        private static TimeZoneInfo GetEasternTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
        }
    }
}
