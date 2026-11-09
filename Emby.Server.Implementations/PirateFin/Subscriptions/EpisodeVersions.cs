using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// The Dub and Sub versions of an anime episode. The episode in the season is one language; the other language is a
/// second, hidden episode item next to it (same folder, "<c>… - [Dub].mkv</c>") whose
/// <see cref="Video.PrimaryVersionId"/> points at the first. Once that version is playable it is added to the
/// episode's <see cref="Video.LinkedAlternateVersions"/>, and Jellyfin shows both in its own version picker.
/// </summary>
/// <remarks>
/// When the dub finishes downloading, the sub version is deleted unless a subscriber of the season wants subs (or the
/// season has no subscribers, i.e. it was downloaded by opening it). Deleting the episode in the season makes Jellyfin
/// promote the dub to take its place.
/// </remarks>
public sealed partial class EpisodeVersions : IEpisodeVersions, IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDownloadManager _downloadManager;
    private readonly ISeasonSubscriptions _subscriptions;
    private readonly IOptionsMonitor<JackettOptions> _jackett;
    private readonly ILogger<EpisodeVersions> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeVersions"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="subscriptions">Which users follow which seasons.</param>
    /// <param name="jackett">The Jackett options.</param>
    /// <param name="logger">The logger.</param>
    public EpisodeVersions(
        ILibraryManager libraryManager,
        IDownloadManager downloadManager,
        ISeasonSubscriptions subscriptions,
        IOptionsMonitor<JackettOptions> jackett,
        ILogger<EpisodeVersions> logger)
    {
        _libraryManager = libraryManager;
        _downloadManager = downloadManager;
        _subscriptions = subscriptions;
        _jackett = jackett;
        _logger = logger;
    }

    /// <summary>
    /// Gets the language of an episode's release (anime episodes found before Dub / Sub existed are subs).
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The language.</returns>
    public static ReleaseLanguage LanguageOf(Episode episode) => episode.ReleaseLanguage ?? ReleaseLanguage.Sub;

    /// <summary>
    /// Gets a value indicating whether a version has a release to download or is downloaded.
    /// </summary>
    /// <param name="episode">The version.</param>
    /// <returns>Whether it has a source.</returns>
    public static bool HasSource(Episode? episode)
        => episode is not null && (episode.IsDownloaded || episode.DownloadStartedUtc.HasValue || !string.IsNullOrEmpty(episode.DownloadUrl));

    /// <inheritdoc />
    public ReleaseLanguage GetDefaultLanguage() => _jackett.CurrentValue.DefaultLanguage;

    /// <inheritdoc />
    public ReleaseLanguage[] GetWantedLanguages(Season season)
    {
        var fallback = _jackett.CurrentValue.DefaultLanguage;
        return _subscriptions.GetSubscribers(season.Id).Select(s => s.Language ?? fallback).Distinct().ToArray();
    }

    /// <inheritdoc />
    public Episode? GetVersion(Episode episode, ReleaseLanguage language)
    {
        if (LanguageOf(episode) == language)
        {
            return episode;
        }

        return episode.AlternateVersionId is { } id && _libraryManager.GetItemById(id) is Episode alternate && LanguageOf(alternate) == language
            ? alternate
            : null;
    }

    /// <inheritdoc />
    public async Task<Episode> UseReleaseAsync(Episode episode, TorrentCandidate release, ReleaseLanguage language, CancellationToken cancellationToken)
    {
        var version = GetVersion(episode, language);
        if (version is null)
        {
            version = CreateAlternate(episode, language);
            episode.AlternateVersionId = version.Id;
            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        version.DownloadUrl = release.MagnetUrl;
        version.Seeders = release.Seeders;
        version.IsDownloadable = true;
        version.NextSourceSearchUtc = null;
        version.ReleaseLanguage = language;
        await version.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAlternateAsync(Episode episode, bool includeDownloaded, CancellationToken cancellationToken)
    {
        if (episode.AlternateVersionId is not { } id)
        {
            return false;
        }

        if (_libraryManager.GetItemById(id) is Episode alternate)
        {
            if (alternate.DownloadStartedUtc.HasValue || _downloadManager.GetPendingDownloads().Contains(alternate.Id) || (alternate.IsDownloaded && !includeDownloaded))
            {
                return false;
            }

            _libraryManager.DeleteItem(alternate, new DeleteOptions { DeleteFileLocation = alternate.IsDownloaded }, true);
        }

        episode.AlternateVersionId = null;
        await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadPlayable += OnPlayable;
        _downloadManager.DownloadCompleted += OnCompleted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadPlayable -= OnPlayable;
        _downloadManager.DownloadCompleted -= OnCompleted;
        return Task.CompletedTask;
    }

    // "Frieren - S01E05.mkv" -> "Frieren - S01E05 - [Dub].mkv", next to it.
    private static string GetAlternatePath(Episode episode, ReleaseLanguage language)
    {
        var folder = Path.GetDirectoryName(episode.Path) ?? string.Empty;
        var name = VersionSuffixRegex().Replace(Path.GetFileNameWithoutExtension(episode.Path) ?? string.Empty, string.Empty);
        return Path.Combine(folder, $"{name} - [{language}].mkv");
    }

    [GeneratedRegex(@"\s-\s\[(Dub|Sub)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffixRegex();

    private Episode CreateAlternate(Episode episode, ReleaseLanguage language)
    {
        var path = GetAlternatePath(episode, language);
        var alternate = new Episode
        {
            Id = _libraryManager.GetNewItemId(path, typeof(Episode)),
            Name = episode.Name,
            OriginalTitle = episode.OriginalTitle,
            AlternateTitle = episode.AlternateTitle,
            Overview = episode.Overview,
            FirstAiring = episode.FirstAiring,
            PremiereDate = episode.PremiereDate,
            Path = path,
            BackupImageUrl = episode.BackupImageUrl,
            IndexNumber = episode.IndexNumber,
            ParentIndexNumber = episode.ParentIndexNumber,
            SeriesId = episode.SeriesId,
            SeriesName = episode.SeriesName,
            SeasonId = episode.SeasonId,
            SeasonName = episode.SeasonName,
            ParentId = episode.ParentId,
            LibraryType = episode.LibraryType,
            Genres = episode.Genres,
            ReleaseLanguage = language,
            IsInternet = true,

            // Hidden from the regular searches; the language version search looks after it.
            Scheduled = false,
            IsPlaceHolder = true,
            IsDownloaded = false,
            IsVirtualItem = true
        };

        // Hidden from episode lists from the start; only linked as a version once it plays.
        alternate.SetPrimaryVersionId(episode.Id);
        _libraryManager.CreateItem(alternate, episode.GetParent());
        _logger.LogInformation("Added a {Language} version of {Series} {Episode}", language, episode.SeriesName, episode.Name);
        return alternate;
    }

    private void OnPlayable(object? sender, BaseItem item) => _ = Run(item, LinkAsync);

    private void OnCompleted(object? sender, BaseItem item) => _ = Run(item, async (version, ct) =>
    {
        await LinkAsync(version, ct).ConfigureAwait(false);
        await RemoveUnneededSubAsync(version, ct).ConfigureAwait(false);
    });

    private async Task Run(BaseItem item, Func<Episode, CancellationToken, Task> action)
    {
        if (item is not Episode episode)
        {
            return;
        }

        try
        {
            await action(episode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the versions of {Series} {Episode}", episode.SeriesName, episode.Name);
        }
    }

    // Shows a hidden version in the episode's version picker.
    private async Task LinkAsync(Episode version, CancellationToken cancellationToken)
    {
        if (version.PrimaryVersionId is not { } primaryId
            || _libraryManager.GetItemById(primaryId) is not Episode primary
            || primary.LinkedAlternateVersions.Any(l => l.ItemId.HasValue && l.ItemId.Value.Equals(version.Id)))
        {
            return;
        }

        primary.LinkedAlternateVersions = [.. primary.LinkedAlternateVersions, new LinkedChild { ItemId = version.Id, Type = LinkedChildType.LinkedAlternateVersion }];
        await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Series} {Episode} now has a {Language} version", primary.SeriesName, primary.Name, LanguageOf(version));
    }

    private async Task RemoveUnneededSubAsync(Episode dub, CancellationToken cancellationToken)
    {
        if (LanguageOf(dub) != ReleaseLanguage.Dub)
        {
            return;
        }

        var primary = dub.PrimaryVersionId is { } id ? _libraryManager.GetItemById(id) as Episode : dub;
        if (primary?.Season is not { } season)
        {
            return;
        }

        var sub = GetVersion(primary, ReleaseLanguage.Sub);
        var wanted = GetWantedLanguages(season);
        if (sub is null || sub.Id.Equals(dub.Id) || wanted.Length == 0 || wanted.Contains(ReleaseLanguage.Sub)
            || sub.DownloadStartedUtc.HasValue || _downloadManager.GetPendingDownloads().Contains(sub.Id))
        {
            return;
        }

        _logger.LogInformation("Removing the sub version of {Series} {Episode}: the dub is downloaded and nobody wants subs", primary.SeriesName, primary.Name);
        if (sub.Id.Equals(primary.Id))
        {
            // Jellyfin promotes the dub to take the episode's place.
            _libraryManager.DeleteItem(primary, new DeleteOptions { DeleteFileLocation = sub.IsDownloaded }, true);
        }
        else
        {
            primary.AlternateVersionId = null;
            await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            _libraryManager.DeleteItem(sub, new DeleteOptions { DeleteFileLocation = sub.IsDownloaded }, true);
        }
    }
}
