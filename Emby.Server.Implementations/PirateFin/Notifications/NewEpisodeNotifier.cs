using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// Tells a season's subscribers (in their bell, and by email when they turned that on) when one of its episodes finishes
/// downloading, but only for episodes that aired after they subscribed: they follow the show as it airs. Catching up on
/// a back catalogue (or subscribing to a whole season found in search) never notifies.
/// Episodes of a series that download together get one email, sent when the first of them (in episode order) is ready
/// and listing them all; a whole season is announced as such. The bell still gets one notification per episode.
/// </summary>
public sealed class NewEpisodeNotifier : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDownloadManager _downloadManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ISeasonSubscriptions _subscriptions;
    private readonly IUserNotificationSettings _settings;
    private readonly IUserInbox _inbox;
    private readonly IEmailSender _emailSender;
    private readonly IEmailAssets _assets;
    private readonly EpisodeEmailBatches _batches;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<NewEpisodeNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewEpisodeNotifier"/> class.
    /// </summary>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="subscriptions">Which users follow which seasons.</param>
    /// <param name="settings">The users' email settings.</param>
    /// <param name="inbox">The users' bell notifications.</param>
    /// <param name="emailSender">The email sender.</param>
    /// <param name="assets">The emails' posters and links.</param>
    /// <param name="batches">The episodes held for, or already listed in, an email.</param>
    /// <param name="configurationManager">The server configuration (for the server name).</param>
    /// <param name="logger">The logger.</param>
    public NewEpisodeNotifier(
        IDownloadManager downloadManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ISeasonSubscriptions subscriptions,
        IUserNotificationSettings settings,
        IUserInbox inbox,
        IEmailSender emailSender,
        IEmailAssets assets,
        EpisodeEmailBatches batches,
        IServerConfigurationManager configurationManager,
        ILogger<NewEpisodeNotifier> logger)
    {
        _downloadManager = downloadManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _subscriptions = subscriptions;
        _settings = settings;
        _inbox = inbox;
        _emailSender = emailSender;
        _assets = assets;
        _batches = batches;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadCompleted += OnDownloadCompleted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadCompleted -= OnDownloadCompleted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Notifies the subscribers of the episode's season who subscribed before it aired.
    /// </summary>
    /// <param name="episode">The downloaded episode.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task NotifyAsync(Episode episode, CancellationToken cancellationToken)
    {
        // One at a time, so episodes finishing together are held / announced consistently.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NotifyFollowersAsync(episode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task NotifyFollowersAsync(Episode episode, CancellationToken cancellationToken)
    {
        foreach (var user in GetFollowers(episode))
        {
            _inbox.Add(user.Id, UserNotificationKind.EpisodeReady, GetSubject(episode), GetTitle(episode), episode.Id);

            var settings = _settings.Get(user.Id);
            if (!_emailSender.IsConfigured || !settings.NewEpisodeEmails || string.IsNullOrWhiteSpace(settings.Email))
            {
                continue;
            }

            var batch = GetEmailBatch(episode, user.Id);
            if (batch is null)
            {
                continue;
            }

            try
            {
                await _emailSender.SendAsync(settings.Email, await BuildEmailAsync(batch, user.Username).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Told {User} that {Count} episode(s) of {Series} are ready", user.Username, batch.Count, episode.SeriesName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not email {User} about {Series} {Episode}", user.Username, episode.SeriesName, episode.Name);
            }
        }
    }

    private static string GetNumber(Episode episode)
        => string.Create(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00}");

    private static (int Season, int Episode) GetOrder(Episode episode) => (episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0);

    private static string GetSubject(Episode episode) => $"{episode.SeriesName} {GetNumber(episode)} is ready to watch";

    private static string? GetTitle(Episode episode) => string.IsNullOrWhiteSpace(episode.Name) ? null : episode.Name;

    private static string JoinSeasons(IReadOnlyList<int> seasons)
        => seasons.Count == 1
            ? seasons[0].ToString(CultureInfo.InvariantCulture)
            : string.Join(", ", seasons.Take(seasons.Count - 1)) + " and " + seasons[^1].ToString(CultureInfo.InvariantCulture);

    private static string? GetDetails(Episode episode)
    {
        var parts = new List<string>();
        if (episode.RunTimeTicks is > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(episode.RunTimeTicks.Value).TotalMinutes))} min"));
        }

        if ((episode.FirstAiring ?? episode.PremiereDate) is { } aired)
        {
            parts.Add("Aired " + aired.ToString("MMM d, yyyy", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // The users who follow the episode's season since before it aired.
    private IEnumerable<User> GetFollowers(Episode episode)
    {
        var aired = episode.FirstAiring ?? episode.PremiereDate;
        if (!aired.HasValue || _libraryManager.GetItemById(episode.SeasonId) is not Season season)
        {
            yield break;
        }

        var airedUtc = ReleaseStatusResolver.ToUtc(aired.Value);
        foreach (var subscriber in _subscriptions.GetSubscribers(season.Id).Where(s => airedUtc >= s.SinceUtc))
        {
            if (_userManager.GetUserById(subscriber.UserId) is { } user)
            {
                yield return user;
            }
        }
    }

    private bool IsFollowedBy(Episode episode, Guid userId) => GetFollowers(episode).Any(u => u.Id.Equals(userId));

    /// <summary>
    /// Works out what the user is emailed now the episode is ready: nothing when an earlier email already listed it, or
    /// while an earlier episode of the series is still downloading (it waits for that one); otherwise the episode with
    /// the others of its series that wait or are still downloading, in episode order.
    /// </summary>
    private List<Episode>? GetEmailBatch(Episode episode, Guid userId)
    {
        if (_batches.TryTakeAnnounced(userId, episode.Id))
        {
            return null;
        }

        var downloading = _downloadManager.GetPendingDownloads()
            .Where(id => !id.Equals(episode.Id))
            .Select(id => _libraryManager.GetItemById(id))
            .OfType<Episode>()
            .Where(e => e.SeriesId.Equals(episode.SeriesId) && IsFollowedBy(e, userId))
            .ToList();

        if (downloading.Any(e => GetOrder(e).CompareTo(GetOrder(episode)) < 0))
        {
            _batches.Hold(userId, episode.Id);
            return null;
        }

        var held = _batches.TakeHeld(userId, id => _libraryManager.GetItemById(id) is Episode e && e.SeriesId.Equals(episode.SeriesId))
            .Select(id => _libraryManager.GetItemById(id))
            .OfType<Episode>();

        _batches.Announce(userId, downloading.Select(e => e.Id));

        return new[] { episode }.Concat(held).Concat(downloading)
            .DistinctBy(e => e.Id)
            .OrderBy(GetOrder)
            .ToList();
    }

    // The seasons the batch has every episode of, or null when it doesn't have every episode of all its seasons.
    private List<int>? GetCompleteSeasons(List<Episode> batch)
    {
        if (batch.Count < 2)
        {
            return null;
        }

        var seasons = new List<int>();
        foreach (var group in batch.GroupBy(e => e.SeasonId))
        {
            var all = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = group.Key,
                IncludeItemTypes = [BaseItemKind.Episode]
            });

            if (all.Count == 0 || all.Any(e => !group.Any(b => b.Id.Equals(e.Id))))
            {
                return null;
            }

            seasons.Add(group.First().ParentIndexNumber ?? 0);
        }

        seasons.Sort();
        return seasons;
    }

    private async Task<PirateFinEmail> BuildEmailAsync(List<Episode> batch, string userName)
    {
        var first = batch[0];
        var series = _libraryManager.GetItemById(first.SeriesId) as Series;
        var poster = await _assets.GetPosterAsync(first).ConfigureAwait(false);
        var link = _assets.GetItemLink(first.Id);
        var server = string.IsNullOrWhiteSpace(_configurationManager.Configuration.ServerName) ? "PirateFin" : _configurationManager.Configuration.ServerName;

        EmailBuilder email;
        if (batch.Count == 1)
        {
            var title = GetTitle(first);
            email = new EmailBuilder(GetSubject(first), $"{title ?? GetNumber(first)} is downloaded and ready on {server}.")
                .Greeting(userName)
                .Poster(poster)
                .Label("New episode")
                .Title(first.SeriesName)
                .Subtitle(title is null ? GetNumber(first) : $"{GetNumber(first)} · {title}");
            if (GetDetails(first) is { } details)
            {
                email.Meta(details);
            }

            if (!string.IsNullOrWhiteSpace(first.Overview))
            {
                email.Text(first.Overview);
            }
        }
        else if (GetCompleteSeasons(batch) is { } seasons)
        {
            var plural = seasons.Count > 1;
            var number = JoinSeasons(seasons);
            var downloaded = plural ? $"Seasons {number} were downloaded" : $"Season {number} was downloaded";
            var meta = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{batch.Count} episodes") };
            if (series is not null)
            {
                if (series.Genres.Length > 0)
                {
                    meta.Add(string.Join(", ", series.Genres.Take(3)));
                }

                if (!string.IsNullOrEmpty(series.OfficialRating))
                {
                    meta.Add(series.OfficialRating);
                }
            }

            email = new EmailBuilder(
                    plural ? $"Seasons {number} of {first.SeriesName} were downloaded" : $"Season {number} of {first.SeriesName} was downloaded",
                    $"The complete season{(plural ? "s" : string.Empty)}, {batch.Count} episodes, {(plural ? "are" : "is")} on {server}.")
                .Greeting(userName)
                .Poster(poster)
                .Label(plural ? "Complete seasons" : "Complete season")
                .Title(first.SeriesName)
                .Subtitle(downloaded)
                .Meta(string.Join(" · ", meta))
                .Text($"The whole season{(plural ? "s are" : " is")} on {server}, starting with {GetNumber(first)}.");
        }
        else
        {
            var seasonNumbers = batch.Select(e => e.ParentIndexNumber ?? 0).Distinct().ToList();
            var count = string.Create(CultureInfo.InvariantCulture, $"{batch.Count} episodes");
            email = new EmailBuilder(
                    $"{batch.Count} new episodes of {first.SeriesName}",
                    string.Join(", ", batch.Select(GetNumber)) + $" are on {server}.")
                .Greeting(userName)
                .Poster(poster)
                .Label("New episodes")
                .Title(first.SeriesName)
                .Meta(seasonNumbers.Count == 1 ? string.Create(CultureInfo.InvariantCulture, $"Season {seasonNumbers[0]} · {count}") : count)
                .EpisodeList(batch.Select(e => (GetNumber(e), GetTitle(e) ?? string.Empty)));
        }

        return email
            .Button("Watch now", link, play: true)
            .Footer("You get this email because you follow this season. To stop these emails, turn off the bell on the season, or switch off new episode emails on your profile page.")
            .Build();
    }

    private void OnDownloadCompleted(object? sender, BaseItem item)
    {
        if (item is Episode episode)
        {
            _ = Task.Run(() => NotifyAsync(episode, CancellationToken.None));
        }
    }
}
