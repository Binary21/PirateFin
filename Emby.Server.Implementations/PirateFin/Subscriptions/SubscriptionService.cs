using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.PirateFin.Models;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// Finds download sources for aired placeholder episodes.
/// </summary>
/// <remarks>
/// <para>From the first search attempt, only the configured release authors are searched for
/// <see cref="SubscriptionOptions.AuthorSearchWindow"/>. After that the top <see cref="SubscriptionOptions.FallbackCount"/>
/// author-less releases are stored for the user to choose from, while the authors keep being retried in the background.</para>
/// <para>An author release is used automatically: subscribed seasons start downloading straight away, other episodes
/// when they are opened. Fallback releases are never downloaded without the user choosing one.</para>
/// </remarks>
public sealed class SubscriptionService : ISubscriptionService
{
    private static readonly TimeSpan _resultsWindow = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    // What recent searches found (for the dashboard), oldest first.
    private readonly ConcurrentQueue<(DateTime At, SearchResult Result)> _results = new();

    private readonly IEpisodeSubscriptionRepository _repository;
    private readonly ITorrentSearchService _torrentSearch;
    private readonly IDownloadManager _downloadManager;
    private readonly ISeasonSubscriptions _subscriptions;
    private readonly IReleaseSearchPauses _pauses;
    private readonly IOptionsMonitor<SubscriptionOptions> _options;
    private readonly IEpisodeVersions _versions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubscriptionService> _logger;
    private readonly DateTime _startedUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    /// <param name="repository">The repository.</param>
    /// <param name="torrentSearch">The torrent search.</param>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="subscriptions">Which users follow which seasons.</param>
    /// <param name="pauses">The paused back catalogue searches.</param>
    /// <param name="options">The options.</param>
    /// <param name="versions">The Dub / Sub versions of anime episodes.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public SubscriptionService(
        IEpisodeSubscriptionRepository repository,
        ITorrentSearchService torrentSearch,
        IDownloadManager downloadManager,
        ISeasonSubscriptions subscriptions,
        IReleaseSearchPauses pauses,
        IOptionsMonitor<SubscriptionOptions> options,
        IEpisodeVersions versions,
        TimeProvider timeProvider,
        ILogger<SubscriptionService> logger)
    {
        _versions = versions;
        _repository = repository;
        _torrentSearch = torrentSearch;
        _downloadManager = downloadManager;
        _subscriptions = subscriptions;
        _pauses = pauses;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _startedUtc = timeProvider.GetUtcNow().UtcDateTime;
    }

    private enum SearchResult
    {
        Preferred,
        FallbackOnly,
        Nothing
    }

    /// <inheritdoc />
    public async Task ProcessDueEpisodesAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // Paused: older episodes only; new ones keep being searched.
        var due = _repository.GetDueEpisodes(now, options.PageSize, e => _pauses.IsPaused(e.SeriesId) && IsOld(e, now, options));
        if (due.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Searching sources for {Count} episodes", due.Count);
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxConcurrency), CancellationToken = cancellationToken },
            async (episode, ct) =>
            {
                if (!_inFlight.TryAdd(episode.Id, 0))
                {
                    return;
                }

                try
                {
                    await ProcessEpisodeAsync(episode, now, options, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Source search failed for {Series} {Episode}", episode.SeriesName, episode.Name);
                }
                finally
                {
                    _inFlight.TryRemove(episode.Id, out _);
                }
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> GetSearchingNow() => _inFlight.Keys.ToList();

    /// <inheritdoc />
    public (int Preferred, int FallbackOnly, int Nothing, DateTime SinceUtc) GetRecentResults()
    {
        var since = _timeProvider.GetUtcNow().UtcDateTime - _resultsWindow;
        Prune(since);
        var results = _results.Where(r => r.At >= since).Select(r => r.Result).ToList();
        return (
            results.Count(r => r == SearchResult.Preferred),
            results.Count(r => r == SearchResult.FallbackOnly),
            results.Count(r => r == SearchResult.Nothing),
            since > _startedUtc ? since : _startedUtc);
    }

    /// <inheritdoc />
    public async Task ProcessSeasonReleasesAsync(SearchedSeries searchedSeries, Series series, CancellationToken cancellationToken)
    {
        var releases = await _torrentSearch.FindSeasonReleasesAsync(searchedSeries, cancellationToken).ConfigureAwait(false);
        if (releases.Count == 0)
        {
            return;
        }

        foreach (var season in series.Children.OfType<Season>())
        {
            if (!releases.TryGetValue(season.IndexNumber ?? 0, out var seasonReleases))
            {
                continue;
            }

            foreach (var episode in season.Children.OfType<Episode>())
            {
                if (!episode.IsDownloaded
                    && string.IsNullOrEmpty(episode.DownloadUrl)
                    && seasonReleases.TryGetValue(episode.IndexNumber ?? 0, out var release))
                {
                    UseSource(episode, release);
                    await _repository.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> SelectSourceAsync(Episode episode, int index, CancellationToken cancellationToken)
    {
        var candidates = episode.SourceCandidates ?? [];
        if (index < 0 || index >= candidates.Length)
        {
            return false;
        }

        UseSource(episode, candidates[index]);
        await _repository.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
        _downloadManager.QueueDownload(episode);
        _logger.LogInformation("User chose {Release} for {Series} {Episode}", candidates[index].Title, episode.SeriesName, episode.Name);
        return true;
    }

    /// <inheritdoc />
    public Task SetSubscribedAsync(Season season, Guid userId, bool subscribed, CancellationToken cancellationToken)
        => SetSubscribedAsync(season, userId, subscribed, null, cancellationToken);

    /// <inheritdoc />
    public async Task SetSubscribedAsync(Season season, Guid userId, bool subscribed, ReleaseLanguage? language, CancellationToken cancellationToken)
    {
        if (!subscribed)
        {
            await _subscriptions.UnsubscribeAsync(season, userId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var anime = season.Children.OfType<Episode>().Any(IsAnime);
        await _subscriptions.SubscribeAsync(season, userId, anime ? language : null, cancellationToken).ConfigureAwait(false);

        if (!anime)
        {
            QueueAvailable(season);
            return;
        }

        // Look for the wanted languages straight away (the season may have ended long ago), then download what's
        // still needed: a sub isn't downloaded for a dub subscriber when the dub was found.
        season.VersionSearchStopped = false;
        season.VersionSearchNextUtc = null;
        await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        _ = Task.Run(() => SearchThenQueueAsync(season), CancellationToken.None);
    }

    private async Task SearchThenQueueAsync(Season season)
    {
        try
        {
            await SearchVersionsAsync(season, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Language version search failed for {Season}", season.Name);
        }

        QueueAvailable(season);
    }

    /// <inheritdoc />
    public async Task ProcessVersionSearchesAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var due = (_repository.GetSubscribedSeasons() ?? [])
            .Where(s => !s.VersionSearchStopped && s.VersionSearchNextUtc is { } next && next <= now)
            .ToList();
        foreach (var season in due)
        {
            if (season.Children.OfType<Episode>().Any(IsAnime))
            {
                await SearchVersionsAsync(season, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<(int Searching, int Replaced, int SkippedDownloading)> ResearchAsync(IEnumerable<Episode> episodes, bool replaceDownloaded, CancellationToken cancellationToken)
    {
        int searching = 0, replaced = 0, skipped = 0;
        var seasons = new Dictionary<Guid, Season>();
        foreach (var episode in episodes)
        {
            if (IsAnime(episode) && episode.Season is { } season)
            {
                // The other-language version is searched again too, by the language version search.
                await _versions.RemoveAlternateAsync(episode, replaceDownloaded, cancellationToken).ConfigureAwait(false);
                seasons.TryAdd(season.Id, season);
            }

            if (!episode.IsInternet && !episode.IsDownloaded)
            {
                // Not a PirateFin episode (e.g. a file the user added).
                continue;
            }

            if (episode.DownloadStartedUtc.HasValue || _downloadManager.GetPendingDownloads().Contains(episode.Id))
            {
                skipped++;
                continue;
            }

            if (episode.IsDownloaded)
            {
                if (!replaceDownloaded)
                {
                    continue;
                }

                DeleteFile(episode.Path);
                episode.IsDownloaded = false;
                episode.IsInternet = true;
                episode.IsPlaceHolder = true;
                episode.IsVirtualItem = true;
                episode.IsPlayable = false;
                episode.RunTimeTicks = null;
                replaced++;
            }

            episode.DownloadUrl = string.Empty;
            episode.Seeders = null;
            episode.IsDownloadable = false;
            episode.SourceCandidates = [];
            episode.SourceCandidatesUpdatedUtc = null;
            episode.SourceSearchStartedUtc = null;
            episode.NextSourceSearchUtc = null;
            episode.Scheduled = true;
            await _repository.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
            searching++;
        }

        foreach (var season in seasons.Values)
        {
            season.VersionSearchStopped = false;
            season.VersionSearchNextUtc = _timeProvider.GetUtcNow().UtcDateTime;
            await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Searching {Count} episodes again ({Replaced} downloaded ones replaced, {Skipped} downloading left alone)", searching, replaced, skipped);
        return (searching, replaced, skipped);
    }

    private static bool IsAnime(Episode episode) => PirateFinLibraries.Normalize(episode.LibraryType) == PirateFinLibraries.Anime;

    private static bool HasSource(Episode? episode)
        => episode is not null && (episode.IsDownloaded || episode.DownloadStartedUtc.HasValue || !string.IsNullOrEmpty(episode.DownloadUrl));

    private void QueueAvailable(Season season)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var wanted = _versions.GetWantedLanguages(season);
        foreach (var episode in season.Children.OfType<Episode>())
        {
            if (!IsAnime(episode) || wanted.Length == 0)
            {
                if (ReleaseStatusResolver.Resolve(episode, now) == ReleaseStatus.Available)
                {
                    _downloadManager.QueueDownload(episode);
                }

                continue;
            }

            // The episode's own release unless nobody wants its language and a wanted version has a release.
            var ownWanted = wanted.Contains(EpisodeVersions.LanguageOf(episode));
            var versions = wanted.Select(l => _versions.GetVersion(episode, l)).Where(HasSource).ToList();
            if ((ownWanted || versions.Count == 0) && ReleaseStatusResolver.Resolve(episode, now) == ReleaseStatus.Available)
            {
                _downloadManager.QueueDownload(episode);
            }

            foreach (var version in versions.Where(v => !v!.Id.Equals(episode.Id) && !v.IsDownloaded))
            {
                _downloadManager.QueueDownload(version!);
            }
        }
    }

    // The languages an anime episode's own release is searched in, in order. Dub first when a subscriber wants dubs
    // (or, for a season nobody follows, when "Prioritize Dub" is on); the sub is the fallback either way.
    private ReleaseLanguage[] GetPrimaryLanguages(Episode episode)
    {
        var wanted = episode.Season is { Subscribed: true } season ? _versions.GetWantedLanguages(season) : [];
        var dubFirst = wanted.Length > 0 ? wanted.Contains(ReleaseLanguage.Dub) : _versions.GetDefaultLanguage() == ReleaseLanguage.Dub;
        return dubFirst ? [ReleaseLanguage.Dub, ReleaseLanguage.Sub] : [ReleaseLanguage.Sub];
    }

    // A new release of the season's episodes is the cue to look for the languages it's missing (dubs follow subs by
    // whole weeks, so the day a sub releases is the day a dub can). At most twice a day.
    private async Task TriggerVersionSearchAsync(Season season, DateTime now, CancellationToken cancellationToken)
    {
        var interval = _options.CurrentValue.VersionSearchInterval;
        var lastSearch = season.VersionSearchNextUtc - interval;
        if (lastSearch > now - TimeSpan.FromHours(12) || (season.VersionSearchNextUtc is { } next && next <= now))
        {
            return;
        }

        season.VersionSearchNextUtc = now;
        await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches the season's episodes for the languages its subscribers want but the episode doesn't have yet, then
    /// schedules the next search a week later (or sooner, when the next sub releases), or stops: once the season has
    /// ended plus one week for each episode the dub is behind, or 4 weeks after it ended when no dub showed up at all.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task SearchVersionsAsync(Season season, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var wanted = _versions.GetWantedLanguages(season);
        var episodes = season.Children.OfType<Episode>()
            .Where(e => !e.PrimaryVersionId.HasValue && IsAnime(e))
            .OrderBy(e => e.IndexNumber)
            .ToList();
        var aired = episodes.Where(e => e.FirstAiring.HasValue && ReleaseStatusResolver.ToUtc(e.FirstAiring.Value) <= now).ToList();

        foreach (var episode in aired)
        {
            // Episodes without any release yet are left to the regular search, which tries the wanted languages too.
            if (!HasSource(episode))
            {
                continue;
            }

            foreach (var language in wanted)
            {
                if (HasSource(_versions.GetVersion(episode, language)))
                {
                    continue;
                }

                var release = await _torrentSearch.FindAuthorReleaseAsync(episode, language, cancellationToken).ConfigureAwait(false);
                if (release is null)
                {
                    continue;
                }

                var version = await _versions.UseReleaseAsync(episode, release, language, cancellationToken).ConfigureAwait(false);
                if (season.Subscribed)
                {
                    _downloadManager.QueueDownload(version);
                }
            }
        }

        int? Latest(ReleaseLanguage language)
            => aired.Where(e => HasSource(_versions.GetVersion(e, language))).Max(e => e.IndexNumber);

        var missing = aired.Any(e => HasSource(e) && wanted.Any(l => !HasSource(_versions.GetVersion(e, l))));
        var ended = episodes.Count > 0 && episodes.Count == aired.Count;
        var seasonEnd = ended ? aired.Max(e => ReleaseStatusResolver.ToUtc(e.FirstAiring!.Value)) : (DateTime?)null;

        var stop = wanted.Length == 0;
        if (!stop && seasonEnd.HasValue)
        {
            var latestDub = Latest(ReleaseLanguage.Dub);
            var weeks = latestDub.HasValue ? Math.Max(0, (Latest(ReleaseLanguage.Sub) ?? 0) - latestDub.Value) : options.NoDubGiveUpWeeks;
            stop = !missing || now > seasonEnd.Value + TimeSpan.FromDays((7 * weeks) + 1);
        }

        season.VersionSearchStopped = stop;
        season.VersionSearchNextUtc = stop ? null : now + options.VersionSearchInterval;
        await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Language versions of {Series} {Season} searched; {Next}",
            season.SeriesName,
            season.Name,
            stop ? "stopped (the dub didn't follow in time, or nothing is missing)" : "next search " + season.VersionSearchNextUtc?.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void DeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private void Record(SearchResult result)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        _results.Enqueue((now, result));
        Prune(now - _resultsWindow);
    }

    private void Prune(DateTime since)
    {
        while (_results.TryPeek(out var oldest) && oldest.At < since)
        {
            _results.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Gets a value indicating whether an episode aired long enough ago to get only one search (the back catalogue).
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="now">The current time, in UTC.</param>
    /// <param name="options">The subscription options.</param>
    /// <returns>Whether it's old.</returns>
    internal static bool IsOld(Episode episode, DateTime now, SubscriptionOptions options)
        => (episode.FirstAiring ?? episode.PremiereDate) is { } aired
            && now - ReleaseStatusResolver.ToUtc(aired) >= options.SingleFallbackAfterAiring;

    private static void UseSource(Episode episode, TorrentCandidate release)
    {
        episode.DownloadUrl = release.MagnetUrl;
        episode.Seeders = release.Seeders;
        episode.IsDownloadable = true;
        episode.NextSourceSearchUtc = null;
    }

    private async Task ProcessEpisodeAsync(Episode episode, DateTime now, SubscriptionOptions options, CancellationToken cancellationToken)
    {
        episode.SourceSearchStartedUtc ??= now;

        var anime = IsAnime(episode);
        TorrentCandidate? release = null;
        if (anime)
        {
            foreach (var language in GetPrimaryLanguages(episode))
            {
                release = await _torrentSearch.FindAuthorReleaseAsync(episode, language, cancellationToken).ConfigureAwait(false);
                if (release is not null)
                {
                    break;
                }
            }
        }
        else
        {
            release = await _torrentSearch.FindAuthorReleaseAsync(episode, cancellationToken).ConfigureAwait(false);
        }

        if (release is not null)
        {
            UseSource(episode, release);
            episode.SourceCandidates = [];
            if (anime)
            {
                episode.ReleaseLanguage = release.Language ?? ReleaseLanguage.Sub;
            }

            await _repository.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
            Record(SearchResult.Preferred);

            if (_repository.IsSeasonSubscribed(episode))
            {
                _downloadManager.QueueDownload(episode);
                if (anime && episode.Season is { } season)
                {
                    await TriggerVersionSearchAsync(season, now, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        var started = episode.SourceSearchStartedUtc.Value;
        var elapsed = now - started;
        if (elapsed >= options.GiveUpAfter)
        {
            _logger.LogInformation("Giving up the source search for {Series} {Episode}", episode.SeriesName, episode.Name);
            episode.Scheduled = false;
            episode.NextSourceSearchUtc = null;
        }
        else if (elapsed < options.AuthorSearchWindow)
        {
            // Retry, but make sure the last author attempt lands right at the end of the window.
            var next = now + options.AuthorRetryInterval;
            var windowEnd = started + options.AuthorSearchWindow;
            episode.NextSourceSearchUtc = next < windowEnd ? next : windowEnd;
        }
        else
        {
            var stale = !episode.SourceCandidatesUpdatedUtc.HasValue
                || now - episode.SourceCandidatesUpdatedUtc.Value >= options.FallbackRefreshInterval;
            if ((episode.SourceCandidates?.Length ?? 0) == 0 || stale)
            {
                var fallback = await _torrentSearch.FindFallbackReleasesAsync(episode, options.FallbackCount, cancellationToken).ConfigureAwait(false);
                episode.SourceCandidates = fallback.ToArray();
                episode.SourceCandidatesUpdatedUtc = now;
                Record(fallback.Count > 0 ? SearchResult.FallbackOnly : SearchResult.Nothing);
            }

            if (IsOld(episode, now, options))
            {
                // Releases of older episodes don't appear later: keep what the one fallback search found and stop.
                _logger.LogInformation(
                    "Stopping the source search for {Series} {Episode} (aired over a week ago); {Count} fallback releases found",
                    episode.SeriesName,
                    episode.Name,
                    episode.SourceCandidates?.Length ?? 0);
                episode.Scheduled = false;
                episode.NextSourceSearchUtc = null;
            }
            else
            {
                episode.NextSourceSearchUtc = now + options.BackgroundAuthorRetryInterval;
            }
        }

        await _repository.SaveAsync(episode, cancellationToken).ConfigureAwait(false);
    }
}
