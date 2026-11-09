using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// Remembers who opened (started, or waits for) each episode / movie download. When a download turns out slow
/// (<see cref="IDownloadManager.IsSlow"/>) they're emailed that it takes a while (the item's page shows a popup); once
/// it can be played they get a bell notification and an email. Fast downloads tell nobody anything. Saved in
/// &lt;data&gt;/piratefin/slow-downloads.json so a restart in the middle of a long download keeps them.
/// </summary>
public sealed class SlowDownloadNotifier : BackgroundService, ISlowDownloads
{
    private static readonly TimeSpan _checkEvery = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IDownloadManager _downloadManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserNotificationSettings _settings;
    private readonly IUserInbox _inbox;
    private readonly IEmailSender _emailSender;
    private readonly IEmailAssets _assets;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<SlowDownloadNotifier> _logger;
    private readonly Lock _lock = new();
    private Dictionary<Guid, Waiting>? _waiting;

    /// <summary>
    /// Initializes a new instance of the <see cref="SlowDownloadNotifier"/> class.
    /// </summary>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="settings">The users' email settings.</param>
    /// <param name="inbox">The users' bell notifications.</param>
    /// <param name="emailSender">The email sender.</param>
    /// <param name="assets">The emails' posters and links.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public SlowDownloadNotifier(
        IDownloadManager downloadManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserNotificationSettings settings,
        IUserInbox inbox,
        IEmailSender emailSender,
        IEmailAssets assets,
        IApplicationPaths appPaths,
        ILogger<SlowDownloadNotifier> logger)
    {
        _downloadManager = downloadManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _settings = settings;
        _inbox = inbox;
        _emailSender = emailSender;
        _assets = assets;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "slow-downloads.json");

    /// <inheritdoc />
    public void Register(Guid userId, BaseItem item)
    {
        bool alreadySlow;
        lock (_lock)
        {
            var waiting = Load();
            if (!waiting.TryGetValue(item.Id, out var entry))
            {
                entry = new Waiting();
                waiting[item.Id] = entry;
            }

            if (entry.Users.Contains(userId))
            {
                return;
            }

            entry.Users.Add(userId);
            alreadySlow = entry.Slow;
            Save();
        }

        // Opened after it was found slow: told straight away, like the others were.
        if (alreadySlow)
        {
            _ = Task.Run(() => EmailSlowAsync(item, [userId], CancellationToken.None));
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadPlayable += OnPlayable;
        _downloadManager.DownloadCompleted += OnPlayable;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _downloadManager.DownloadPlayable -= OnPlayable;
        _downloadManager.DownloadCompleted -= OnPlayable;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Finds the downloads that turned slow and emails their users; forgets downloads that stopped.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        var pending = _downloadManager.GetPendingDownloads();
        var turnedSlow = new List<(Guid ItemId, Guid[] Users)>();
        lock (_lock)
        {
            var waiting = Load();
            var changed = false;
            foreach (var (itemId, entry) in waiting.ToList())
            {
                if (!pending.Contains(itemId))
                {
                    // Cancelled or failed (finished downloads are handled when they become playable).
                    waiting.Remove(itemId);
                    changed = true;
                }
                else if (!entry.Slow && _downloadManager.IsSlow(itemId))
                {
                    entry.Slow = true;
                    changed = true;
                    turnedSlow.Add((itemId, entry.Users.ToArray()));
                }
            }

            if (changed)
            {
                Save();
            }
        }

        foreach (var (itemId, users) in turnedSlow)
        {
            if (_libraryManager.GetItemById(itemId) is { } item)
            {
                _logger.LogInformation("The download of {Name} is slow; telling {Count} user(s)", item.Name, users.Length);
                await EmailSlowAsync(item, users, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A download became playable (or finished): the users of a slow one are told it's ready.
    /// </summary>
    /// <param name="item">The episode or movie.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task NotifyReadyAsync(BaseItem item, CancellationToken cancellationToken)
    {
        Waiting? entry;
        lock (_lock)
        {
            if (!Load().Remove(item.Id, out entry))
            {
                return;
            }

            Save();
        }

        if (!entry.Slow)
        {
            return;
        }

        var title = GetTitle(item);
        foreach (var userId in entry.Users)
        {
            if (_userManager.GetUserById(userId) is not { } user)
            {
                continue;
            }

            _inbox.Add(userId, UserNotificationKind.DownloadReady, $"{title} is ready to watch", "The slow download can be played now.", item.Id);

            var email = _settings.Get(userId).Email;
            if (!_emailSender.IsConfigured || string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            try
            {
                var heading = new EmailBuilder($"{title} is ready to watch", "Your slow download can be played now.")
                    .Greeting(user.Username)
                    .Poster(await _assets.GetPosterAsync(item).ConfigureAwait(false))
                    .Label("Ready to watch", EmailBuilder.Green);
                var message = Describe(heading, item)
                    .Text("The download that was taking longer than usual can be played now.")
                    .Button("Watch now", _assets.GetItemLink(item.Id), play: true)
                    .Footer("You get this email because you started this download.")
                    .Build();
                await _emailSender.SendAsync(email, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not email {User} that {Name} is ready", user.Username, item.Name);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_checkEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Checking for slow downloads failed");
            }
        }
    }

    // "Show S01E02" for an episode, the name for a movie.
    private static string GetTitle(BaseItem item)
        => item is Episode episode
            ? string.Create(CultureInfo.InvariantCulture, $"{episode.SeriesName} S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00}")
            : item.Name;

    // The label is added by the caller; then the series (or movie) name and the episode.
    private static EmailBuilder Describe(EmailBuilder email, BaseItem item)
    {
        if (item is Episode episode)
        {
            var number = string.Create(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00}");
            return email.Title(episode.SeriesName).Subtitle(string.IsNullOrWhiteSpace(episode.Name) ? number : $"{number} · {episode.Name}");
        }

        return email.Title(item.Name);
    }

    private static string FormatTimeLeft(TimeSpan? left)
    {
        if (left is not { } value)
        {
            return "No time estimate yet";
        }

        if (value.TotalHours >= 1.5)
        {
            return string.Create(CultureInfo.InvariantCulture, $"About {Math.Round(value.TotalHours)} hours left");
        }

        return "About an hour left";
    }

    private async Task EmailSlowAsync(BaseItem item, IReadOnlyCollection<Guid> users, CancellationToken cancellationToken)
    {
        if (!_emailSender.IsConfigured)
        {
            return;
        }

        var poster = await _assets.GetPosterAsync(item).ConfigureAwait(false);
        var timeLeft = FormatTimeLeft(_downloadManager.GetTimeLeft(item.Id));
        foreach (var userId in users)
        {
            var email = _settings.Get(userId).Email;
            if (string.IsNullOrWhiteSpace(email) || _userManager.GetUserById(userId) is not { } user)
            {
                continue;
            }

            try
            {
                var message = new EmailBuilder($"{GetTitle(item)} may take a while", "This download is slower than usual; we'll tell you when it's ready.")
                    .Greeting(user.Username)
                    .Poster(poster)
                    .Label("Taking longer than usual", EmailBuilder.Yellow);
                await _emailSender.SendAsync(
                    email,
                    Describe(message, item)
                        .Meta(timeLeft)
                        .Text("This download is slower than usual. Come back later. You'll get another email (and a bell notification) as soon as it's ready to watch.")
                        .Footer("You get this email because you started this download.")
                        .Build(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not email {User} that {Name} is slow", user.Username, item.Name);
            }
        }
    }

    private void OnPlayable(object? sender, BaseItem item)
        => _ = Task.Run(() => NotifyReadyAsync(item, CancellationToken.None));

    private Dictionary<Guid, Waiting> Load()
    {
        if (_waiting is not null)
        {
            return _waiting;
        }

        try
        {
            _waiting = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<Guid, Waiting>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; starting without slow downloads", FilePath);
            _waiting = [];
        }

        return _waiting;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
            File.Move(temp, FilePath, true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not save the slow downloads");
        }
    }

    /// <summary>
    /// The users waiting for one download, and whether it was found slow.
    /// </summary>
    internal sealed class Waiting
    {
        public List<Guid> Users { get; set; } = [];

        public bool Slow { get; set; }
    }
}
