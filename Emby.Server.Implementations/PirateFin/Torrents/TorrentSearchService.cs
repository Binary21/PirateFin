using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Library;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Torrents;

/// <summary>
/// Author and fallback torrent search for episodes.
/// </summary>
public sealed class TorrentSearchService : ITorrentSearchService
{
    private readonly JackettClient _jackett;
    private readonly IOptionsMonitor<JackettOptions> _options;
    private readonly ILogger<TorrentSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TorrentSearchService"/> class.
    /// </summary>
    /// <param name="jackett">The Jackett client.</param>
    /// <param name="options">The Jackett options.</param>
    /// <param name="logger">The logger.</param>
    public TorrentSearchService(JackettClient jackett, IOptionsMonitor<JackettOptions> options, ILogger<TorrentSearchService> logger)
    {
        _jackett = jackett;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string GetDefaultSearchTitle(Series series)
        => new[] { Path.GetFileName(series.Path ?? string.Empty), series.Name }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => ReleaseTitleParser.CleanQueryText(t!))
            .FirstOrDefault(t => t.Length > 0) ?? string.Empty;

    /// <inheritdoc />
    public string CleanSearchText(string value) => ReleaseTitleParser.CleanQueryText(value);

    /// <inheritdoc />
    public Task<TorrentCandidate?> FindAuthorReleaseAsync(Episode episode, CancellationToken cancellationToken)
        => FindAuthorReleaseAsync(episode, ReleaseLanguage.Sub, cancellationToken);

    /// <inheritdoc />
    public Task<TorrentCandidate?> FindAuthorReleaseAsync(Episode episode, ReleaseLanguage language, CancellationToken cancellationToken)
        => FindAuthorReleaseAsync(episode, episode.Series, episode.Season, language, cancellationToken);

    /// <summary>
    /// Searches the preferred groups for the episode, with its series and season given (for tests).
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="series">Its series (search title override).</param>
    /// <param name="season">Its season (search season number override).</param>
    /// <param name="language">The anime group list to search.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release, or <c>null</c>.</returns>
    internal async Task<TorrentCandidate?> FindAuthorReleaseAsync(Episode episode, Series? series, Season? season, ReleaseLanguage language, CancellationToken cancellationToken)
    {
        var context = SearchContext.Create(episode, series, season, _options.CurrentValue, language);
        if (context is null)
        {
            return null;
        }

        foreach (var group in context.Groups)
        {
            foreach (var title in context.Titles)
            {
                foreach (var query in context.BuildQueries(group, title))
                {
                    var results = await _jackett.SearchAsync(query, context.Indexer, context.Category, cancellationToken).ConfigureAwait(false);
                    var best = results
                        .Where(r => ReleaseTitleParser.IsByAuthor(r.Title, group.Name)
                            && HasIndicator(r.Title, group)
                            && context.IsRequestedEpisode(ReleaseTitleParser.Parse(r.Title), title)
                            && ReleaseTitleParser.MentionsTitle(r.Title, title))
                        .MaxBy(r => r.Seeders);

                    if (best is not null)
                    {
                        _logger.LogInformation("Found {Author} release for {Series} S{Season}E{Episode}: {Release}", group.Name, context.Titles[0], context.Season, context.Number, best.Title);
                        return best with { Author = group.Name, Language = context.Language };
                    }
                }
            }
        }

        _logger.LogDebug("No {Language} author release yet for {Series} S{Season}E{Episode}", context.Language?.ToString() ?? "TV", context.Titles[0], context.Season, context.Number);
        return null;
    }

    // A release must carry every word of the group's language indicator (e.g. "English Dub").
    private static bool HasIndicator(string releaseTitle, ReleaseGroup group)
        => string.IsNullOrWhiteSpace(group.Indicator) || ReleaseTitleParser.IsByAuthor(releaseTitle, group.Indicator);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TorrentCandidate>> FindFallbackReleasesAsync(Episode episode, int count, CancellationToken cancellationToken)
    {
        var context = SearchContext.Create(episode, episode.Series, episode.Season, _options.CurrentValue, null);
        if (context is null)
        {
            return [];
        }

        var found = new Dictionary<string, TorrentCandidate>(StringComparer.Ordinal);
        foreach (var title in context.Titles)
        {
            foreach (var query in context.BuildQueries(null, title))
            {
                var results = await _jackett.SearchAsync(query, context.Indexer, context.Category, cancellationToken).ConfigureAwait(false);
                foreach (var result in results)
                {
                    var info = ReleaseTitleParser.Parse(result.Title);
                    if (ReleaseTitleParser.IsOtherContent(info, context.Season, context.Number)
                        || !context.IsSeasonPlausible(info, title)
                        || !ReleaseTitleParser.MentionsTitle(result.Title, title))
                    {
                        continue;
                    }

                    var key = ReleaseTitleParser.GetInfoHash(result.MagnetUrl);
                    if (key.Length == 0)
                    {
                        key = result.MagnetUrl;
                    }

                    if (!found.TryGetValue(key, out var existing) || existing.Seeders < result.Seeders)
                    {
                        found[key] = result;
                    }
                }
            }
        }

        var top = found.Values.OrderByDescending(r => r.Seeders).Take(count).ToList();
        _logger.LogInformation("Found {Count} fallback releases for {Series} S{Season}E{Episode}", top.Count, context.Titles[0], context.Season, context.Number);
        return top;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<int, TorrentCandidate>>> FindSeasonReleasesAsync(SearchedSeries series, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var results = new Dictionary<int, IReadOnlyDictionary<int, TorrentCandidate>>();
        if (string.IsNullOrWhiteSpace(series.Title) || !options.Indexer.TryGetValue(PirateFinLibraries.Shows, out var indexer))
        {
            return results;
        }

        var title = ReleaseTitleParser.CleanQueryText(PirateFinLibrary.SanitizeFileName(series.Title));
        foreach (var season in series.Seasons)
        {
            var episodes = new Dictionary<int, TorrentCandidate>();
            foreach (var author in options.ShowAuthors)
            {
                var query = string.Create(CultureInfo.InvariantCulture, $"{author} {title} S{season.Season:00}");
                var releases = await _jackett.SearchAsync(query, indexer, options.ShowCategory, cancellationToken).ConfigureAwait(false);
                foreach (var release in releases)
                {
                    var info = ReleaseTitleParser.Parse(release.Title);
                    if (info.IsPack
                        || info.Season != season.Season
                        || info.Episode is not { } number
                        || !ReleaseTitleParser.IsByAuthor(release.Title, author)
                        || !ReleaseTitleParser.MentionsTitle(release.Title, title))
                    {
                        continue;
                    }

                    if (!episodes.TryGetValue(number, out var existing) || existing.Seeders < release.Seeders)
                    {
                        episodes[number] = release with { Author = author };
                    }
                }

                if (episodes.Count >= season.Episodes.Count)
                {
                    break;
                }
            }

            results[season.Season] = episodes;
        }

        return results;
    }

    private sealed class SearchContext
    {
        private SearchContext(bool isAnime, string[] titles, string? seasonTitle, ReleaseGroup[] groups, ReleaseLanguage? language, string indexer, string category, int season, int number)
        {
            IsAnime = isAnime;
            Titles = titles;
            SeasonTitle = seasonTitle;
            Groups = groups;
            Language = language;
            Indexer = indexer;
            Category = category;
            Season = season;
            Number = number;
        }

        public bool IsAnime { get; }

        // Anime seasons are separate AniList entries with their own title; fansub releases of later seasons often
        // carry only that title and no season number.
        public string? SeasonTitle { get; }

        public string[] Titles { get; }

        public ReleaseGroup[] Groups { get; }

        // The anime list the groups come from; null for TV.
        public ReleaseLanguage? Language { get; }

        public string Indexer { get; }

        public string Category { get; }

        public int Season { get; }

        public int Number { get; }

        public static SearchContext? Create(Episode episode, Series? series, Season? seasonItem, JackettOptions options, ReleaseLanguage? language)
        {
            var library = PirateFinLibraries.Normalize(episode.LibraryType);
            var number = episode.IndexNumber ?? 0;
            if (library is null || library == PirateFinLibraries.Movies || number <= 0 || !options.Indexer.TryGetValue(library, out var indexer))
            {
                return null;
            }

            var isAnime = library == PirateFinLibraries.Anime;

            // The series folder keeps the Trakt / AniList title even after metadata providers rename the series,
            // and that title is what release groups use.
            // An admin's search title replaces every default title (and with it the season's own title).
            var titleOverride = series?.SearchTitleOverride;
            var hasTitleOverride = !string.IsNullOrWhiteSpace(titleOverride);
            var folderTitle = Path.GetFileName(series?.Path ?? string.Empty);
            var titles = (hasTitleOverride ? new[] { titleOverride } : new[] { folderTitle, episode.SeriesName, isAnime ? episode.AlternateTitle : null })
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => ReleaseTitleParser.CleanQueryText(t!))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (titles.Length == 0)
            {
                return null;
            }

            var season = seasonItem?.SearchSeasonNumberOverride ?? episode.ParentIndexNumber ?? episode.AiredSeasonNumber ?? 1;
            var seasonTitle = isAnime && !hasTitleOverride && season > 1 && !string.IsNullOrWhiteSpace(episode.AlternateTitle)
                ? ReleaseTitleParser.CleanQueryText(episode.AlternateTitle)
                : null;

            var groups = isAnime
                ? options.GetAnimeGroups(language ?? ReleaseLanguage.Sub)
                : options.ShowAuthors.Select(a => new ReleaseGroup { Name = a }).ToArray();

            return new SearchContext(
                isAnime,
                titles,
                seasonTitle,
                groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)).ToArray(),
                isAnime ? language ?? ReleaseLanguage.Sub : null,
                indexer,
                isAnime ? options.AnimeCategory : options.ShowCategory,
                season,
                number);
        }

        public bool IsRequestedEpisode(ReleaseInfo info, string searchedTitle)
            => ReleaseTitleParser.IsEpisode(info, Season, Number) && IsSeasonPlausible(info, searchedTitle);

        // A release without a season number is only season 1, unless it was found under the season's own title.
        public bool IsSeasonPlausible(ReleaseInfo info, string searchedTitle)
            => info.Season.HasValue
                || Season == 1
                || string.Equals(searchedTitle, SeasonTitle, StringComparison.OrdinalIgnoreCase);

        // Scene releases use S01E05; fansub groups use "Title - 05", which a S01E05 query never matches.
        // The group's language indicator goes after the title, before the episode number.
        public IEnumerable<string> BuildQueries(ReleaseGroup? group, string title)
        {
            var prefix = string.Join(' ', new[] { group?.Name, title, group?.Indicator }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
            yield return string.Create(CultureInfo.InvariantCulture, $"{prefix} S{Season:00}E{Number:00}");
            if (IsAnime)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{prefix} {Number:00}");
            }
        }
    }
}
