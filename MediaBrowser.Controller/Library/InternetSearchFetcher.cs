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
    public class InternetSearchFetcher : IInternetSearchFetcher
    {
        private static readonly Regex _seasonSuffixRegex = new Regex(@"\s+Season\s+(?<season>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _ordinalSeasonSuffixRegex = new Regex(@"\s+\d+(st|nd|rd|th)\s+Season\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HttpClient _traktClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 50,
            EnableMultipleHttp2Connections = true
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private const string AniListAnimeSearchQuery = @"query SearchAnime($search: String) { Page(page: 1, perPage: 25) { media(search: $search, type: ANIME, format_in: [TV]) { id idMal title { romaji english native } format status episodes source genres seasonYear startDate { year month day } type coverImage { large } relations { edges { relationType(version: 2) node { id format type } } } airingSchedule(notYetAired: false, perPage: 50) { nodes { episode airingAt timeUntilAiring } } } } }";

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
        /// Initializes a new instance of the <see cref="InternetSearchFetcher"/> class.
        /// </summary>
        /// <param name="options">The provider options.</param>
        public InternetSearchFetcher(IOptions<InternetProviderOptions> options)
        {
            _options = options.Value;
        }

        public async Task<List<SearchedSeries>> GetTVShowSeries(string searchQuery)
        {
            List<SearchedSeries> searchedSeries = [];
            var query = Uri.EscapeDataString(searchQuery);
            var rawUrl = $"{_options.TraktApiUrl}/search/show?query={query}";
            var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.TraktApiKey);
            request.Headers.Add("User-Agent", _options.TraktApiUser);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");

            var response = await _traktClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return searchedSeries;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("show", out var show))
                {
                    continue;
                }

                // var genres = GetGenres(show);
                // if (genres.Any(g => _options.BannedShowGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                // {
                //     continue;
                // }

                // if (!genres.Any(g => _options.AllowedShowGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                // {
                //     continue;
                // }

                // var network = show.TryGetProperty("network", out var networkElement) &&
                //          networkElement.ValueKind == JsonValueKind.String
                // ? networkElement.GetString() : string.Empty;
                // if (!_options.AllowedShowNetworks.Contains(network, StringComparer.OrdinalIgnoreCase))
                // {
                //     continue;
                // }

                var ids = show.GetProperty("ids");
                show.TryGetProperty("images", out var images);
                images.TryGetProperty("poster", out var posters);
                string poster = string.Empty;
                if (posters.GetArrayLength() > 0)
                {
                    poster = posters[0].ToString();
                }

                SearchedSeries tempSeries = new SearchedSeries
                {
                    Title = GetStringProperty(show, "title"),
                    AlternateTitle = GetStringProperty(ids, "slug"),
                    ImageUrl = poster,
                    Genres = GetGenres(show)
                };

                tempSeries.Seasons = await GetTVShowSeasons(tempSeries.AlternateTitle, tempSeries.Genres).ConfigureAwait(false);
                searchedSeries.Add(tempSeries);
            }

            return searchedSeries;
        }

        private async Task<List<SearchedSeason>> GetTVShowSeasons(string searchQuery, string[] genres)
        {
            var slugUrl = $"{_options.TraktApiUrl}/shows/{searchQuery}/seasons?extended=full,episodes";
            var request = new HttpRequestMessage(HttpMethod.Get, slugUrl);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Add("trakt-api-key", _options.TraktApiKey);
            request.Headers.Add("User-Agent", _options.TraktApiUser);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            List<SearchedSeason> searchedSeasons = [];

            var response = await _traktClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return searchedSeasons;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (GetStringProperty(item, "title") == "Specials")
                {
                    continue;
                }

                var tempSeason = new SearchedSeason
                {
                    Title = "Season " + (GetInt32Property(item, "number") ?? 0).ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    Overview = GetStringProperty(item, "overview"),
                    Season = GetInt32Property(item, "number") ?? 0,
                    Genres = genres
                };

                item.TryGetProperty("episodes", out var episodes);
                if (episodes.GetArrayLength() > 0)
                {
                    List<SearchedEpisode> tempEpisodes = [];
                    foreach (JsonElement episode in episodes.EnumerateArray())
                    {
                        var episodeAirDate = episode.TryGetProperty("updated_at", out var updatedAtProp) &&
                                                updatedAtProp.ValueKind == JsonValueKind.String &&
                                                DateTimeOffset.TryParse(updatedAtProp.GetString(), out var parsed)
                                                ? parsed.LocalDateTime : new DateTime(2000, 1, 1);
                        SearchedEpisode tempEpisode = new SearchedEpisode
                        {
                            Episode = GetInt32Property(episode, "number") ?? 0,
                            FirstAiring = episodeAirDate,
                            Overview = GetStringProperty(episode, "overview"),
                            Title = GetStringProperty(episode, "title"),
                        };
                        tempEpisodes.Add(tempEpisode);
                    }

                    tempSeason.Episodes = tempEpisodes;
                }

                searchedSeasons.Add(tempSeason);
            }

            return searchedSeasons;
        }

        public async Task<SearchedSeries> GetAnimeSeries(string searchQuery)
        {
            var requestBody = new
            {
                query = AniListAnimeSearchQuery,
                variables = new
                {
                    search = searchQuery
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

            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("Page", out var page) || !page.TryGetProperty("media", out var media))
            {
                return new SearchedSeries();
            }

            var chain = BuildSeasonChain(media);
            if (chain.Count == 0)
            {
                return new SearchedSeries();
            }

            var searchedSeries = new SearchedSeries
            {
                Title = GetPreferredTitle(chain[0]),
                AlternateTitle = GetRomajiTitle(chain[0]),
                ImageUrl = GetStringProperty(chain[0], "coverImage", "large"),
                Genres = GetGenres(chain[0]),
                AniListId = GetInt32Property(chain[0], "id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            };

            var searchedSeasons = new List<SearchedSeason>();
            foreach (JsonElement season in chain)
            {
                BuildSeasonsList(season, searchedSeasons);
            }

            searchedSeries.Seasons = searchedSeasons;
            return searchedSeries;
        }

        private static void BuildSeasonsList(JsonElement season, List<SearchedSeason> searchedSeasons)
        {
            var tempSeason = new SearchedSeason
            {
                SeasonYear = GetInt32Property(season, "seasonYear"),
                Season = searchedSeasons.Count + 1
            };
            tempSeason.Title = "Season " + tempSeason.Season;
            tempSeason.AlternateTitle = GetPreferredTitle(season);
            tempSeason.RomajiTitle = GetRomajiTitle(season);
            tempSeason.ImageUrl = GetStringProperty(season, "coverImage", "large");
            tempSeason.Genres = GetGenres(season);

            if (season.TryGetProperty("airingSchedule", out var airingSchedule) &&
                airingSchedule.ValueKind == JsonValueKind.Object &&
                airingSchedule.TryGetProperty("nodes", out var nodes) &&
                nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement episode in nodes.EnumerateArray())
                {
                    tempSeason.Episodes.Add(BuildEpisodesList(episode));
                }
            }

            // AniList sometimes has no schedule for older shows, but still has the
            // start date and episode count. Fill the gap from those.
            if (tempSeason.Episodes.Count == 0)
            {
                PopulateEpisodesFromStartDate(season, tempSeason);
            }

            searchedSeasons.Add(tempSeason);
        }

        private static SearchedEpisode BuildEpisodesList(JsonElement episode)
        {
            var tempEpisode = new SearchedEpisode();

            tempEpisode.Episode = GetInt32Property(episode, "episode") ?? 0;
            tempEpisode.FirstAiring = GetUnixTimeProperty(episode, "airingAt")?.AddHours(-4);
            return tempEpisode;
        }

        private static List<JsonElement> BuildSeasonChain(JsonElement media)
        {
            // TV only, already released
            var candidates = media.EnumerateArray()
                .Where(m => GetStringProperty(m, "format") == "TV"
                        && GetStringProperty(m, "status") != "NOT_YET_RELEASED")
                .ToList();

            // Drop spinoffs: an entry that points UP to a parent anime (e.g. Junior High)
            var mainSeries = candidates
                .Where(m => !GetRelations(m).Any(r => r.Type == "PARENT" && r.NodeType == "ANIME"))
                .ToDictionary(m => m.GetProperty("id").GetInt32());

            JsonElement? NextOf(JsonElement m, string relationType)
            {
                foreach (var r in GetRelations(m))
                {
                    if (r.Type == relationType && mainSeries.TryGetValue(r.Id, out var related))
                    {
                        return related;
                    }
                }

                return null;
            }

            List<JsonElement> WalkFrom(JsonElement start)
            {
                var result = new List<JsonElement>();
                var seen = new HashSet<int>();

                JsonElement? cur = start;
                while (cur is JsonElement el && seen.Add(el.GetProperty("id").GetInt32()))
                {
                    result.Add(el);
                    cur = NextOf(el, "SEQUEL");
                }

                return result;
            }

            // Roots = main entries with no prequel in the result set.
            // If several remain, the longest chain wins.
            return mainSeries.Values
                .Where(m => NextOf(m, "PREQUEL") == null)
                .Select(WalkFrom)
                .OrderByDescending(c => c.Count)
                .FirstOrDefault() ?? new List<JsonElement>();
        }

        private static void PopulateEpisodesFromStartDate(JsonElement season, SearchedSeason target)
        {
            if (GetStringProperty(season, "status") != "FINISHED")
            {
                return;
            }

            if (!season.TryGetProperty("episodes", out var episodesEl) ||
                episodesEl.ValueKind != JsonValueKind.Number ||
                episodesEl.GetInt32() <= 0)
            {
                return;
            }

            if (!season.TryGetProperty("startDate", out var start) || start.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            int? year = GetNullableInt(start, "year");
            if (year is null)
            {
                return;
            }

            // month/day can be null on AniList, so fall back to the 1st
            int month = GetNullableInt(start, "month") ?? 1;
            int day = GetNullableInt(start, "day") ?? 1;

            DateTime firstAired;
            try
            {
                firstAired = new DateTime(year.Value, month, day, 0, 0, 0);
            }
            catch (ArgumentOutOfRangeException)
            {
                firstAired = new DateTime(2000, 09, 01, 0, 0, 0); // Developers Birthday!!!
            }

            if (firstAired > DateTimeOffset.UtcNow)
            {
                return; // not aired yet, don't invent episodes
            }

            for (int ep = 1; ep <= episodesEl.GetInt32(); ep++)
            {
                target.Episodes.Add(new SearchedEpisode
                {
                    Episode = ep,
                    FirstAiring = firstAired
                });
            }
        }

        private static int? GetNullableInt(JsonElement parent, string name)
        {
            return parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
                ? el.GetInt32()
                : null;
        }

        private static IEnumerable<(string Type, int Id, string NodeType)> GetRelations(JsonElement media)
        {
            if (!media.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Object ||
                !relations.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (JsonElement edge in edges.EnumerateArray())
            {
                var node = edge.GetProperty("node");
                yield return (
                    edge.GetProperty("relationType").GetString() ?? string.Empty,
                    node.GetProperty("id").GetInt32(),
                    node.GetProperty("type").GetString() ?? string.Empty);
            }
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

        private static DateTime? GetUnixTimeProperty(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value) || !value.TryGetInt32(out var unixTime))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
        }

        private static int? GetInt32Property(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var intValue)
                ? intValue
                : null;
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
    }
}
