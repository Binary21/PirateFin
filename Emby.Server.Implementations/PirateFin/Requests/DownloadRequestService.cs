using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Notifications;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Requests;

/// <summary>
/// Users' requests for episodes PirateFin found no release for, saved in &lt;data&gt;/piratefin/requests.json with the
/// users' request limits. Admins are emailed about new requests; users get a bell notification (and an email) about
/// their closed ones.
/// </summary>
public sealed class DownloadRequestService : IDownloadRequests
{
    private const int DefaultRequestLimit = 3;

    private static readonly TimeSpan _closedShownFor = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDownloadManager _downloadManager;
    private readonly IEmailSender _emailSender;
    private readonly IEmailAssets _assets;
    private readonly IUserNotificationSettings _notificationSettings;
    private readonly IUserInbox _inbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DownloadRequestService> _logger;
    private readonly Lock _lock = new();
    private StoreData? _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadRequestService"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="downloadManager">The download manager.</param>
    /// <param name="emailSender">The email sender.</param>
    /// <param name="assets">The emails' posters and links.</param>
    /// <param name="notificationSettings">The users' email settings.</param>
    /// <param name="inbox">The users' bell notifications.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public DownloadRequestService(
        IApplicationPaths appPaths,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDownloadManager downloadManager,
        IEmailSender emailSender,
        IEmailAssets assets,
        IUserNotificationSettings notificationSettings,
        IUserInbox inbox,
        TimeProvider timeProvider,
        ILogger<DownloadRequestService> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _downloadManager = downloadManager;
        _emailSender = emailSender;
        _assets = assets;
        _notificationSettings = notificationSettings;
        _inbox = inbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public int DefaultLimit
    {
        get
        {
            lock (_lock)
            {
                return Load().DefaultLimit;
            }
        }

        set
        {
            lock (_lock)
            {
                Load().DefaultLimit = Math.Max(-1, value);
                Save();
            }
        }
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "requests.json");

    /// <inheritdoc />
    public async Task<DownloadRequest> CreateAsync(User user, BaseItem item, CancellationToken cancellationToken)
    {
        var (seriesName, title, episodes) = item switch
        {
            Episode episode => (episode.SeriesName, Label(episode), IsMissing(episode) ? [episode] : []),
            Season season => (season.SeriesName, season.Name, GetMissingEpisodes(season)),
            _ => throw new InvalidOperationException("Only episodes and seasons can be requested.")
        };
        if (episodes.Count == 0)
        {
            throw new InvalidOperationException(item is Season
                ? "Nothing to request: every aired episode of this season is downloaded or still being looked for."
                : "Nothing to request: PirateFin hasn't given up looking for this episode.");
        }

        DownloadRequest request;
        lock (_lock)
        {
            var data = Load();
            if (data.Requests.Any(r => r.Status == DownloadRequestStatus.Open && r.UserId.Equals(user.Id) && r.ItemId.Equals(item.Id)))
            {
                throw new InvalidOperationException("You already asked for this; it's waiting for an admin.");
            }

            var quota = GetQuota(user.Id);
            if (quota.Remaining == 0)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"You have {quota.Open} open requests, the most you can have. Wait for an admin to close one, or cancel one."));
            }

            request = new DownloadRequest
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                UserName = user.Username,
                ItemId = item.Id,
                SeriesName = seriesName ?? string.Empty,
                Title = title ?? string.Empty,
                CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime,
                Status = DownloadRequestStatus.Open,
                Episodes = episodes.Select(e => new RequestedEpisode { EpisodeId = e.Id, Label = Label(e) }).ToList()
            };
            data.Requests.Add(request);
            Save();
        }

        _logger.LogInformation("{User} requested {Series} {Title}", user.Username, request.SeriesName, request.Title);
        await EmailAdminsAsync(request, cancellationToken).ConfigureAwait(false);
        return request;
    }

    /// <inheritdoc />
    public IReadOnlyList<DownloadRequest> GetAll(bool includeClosed)
    {
        lock (_lock)
        {
            return Load().Requests
                .Where(r => includeClosed || r.Status == DownloadRequestStatus.Open)
                .OrderByDescending(r => r.CreatedUtc)
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DownloadRequest> GetForUser(Guid userId)
    {
        var since = _timeProvider.GetUtcNow().UtcDateTime - _closedShownFor;
        lock (_lock)
        {
            return Load().Requests
                .Where(r => r.UserId.Equals(userId) && (r.Status == DownloadRequestStatus.Open || r.ClosedUtc >= since))
                .OrderByDescending(r => r.CreatedUtc)
                .ToList();
        }
    }

    /// <inheritdoc />
    public int CountOpen()
    {
        lock (_lock)
        {
            return Load().Requests.Count(r => r.Status == DownloadRequestStatus.Open);
        }
    }

    /// <inheritdoc />
    public RequestQuota GetQuota(Guid userId)
    {
        lock (_lock)
        {
            var data = Load();
            var limit = data.Limits.TryGetValue(Key(userId), out var own) ? own : data.DefaultLimit;
            return new RequestQuota(limit, data.Requests.Count(r => r.UserId.Equals(userId) && r.Status == DownloadRequestStatus.Open));
        }
    }

    /// <inheritdoc />
    public int? GetLimit(Guid userId)
    {
        lock (_lock)
        {
            return Load().Limits.TryGetValue(Key(userId), out var limit) ? limit : null;
        }
    }

    /// <inheritdoc />
    public void SetLimit(Guid userId, int? limit)
    {
        lock (_lock)
        {
            if (limit is { } value)
            {
                Load().Limits[Key(userId)] = Math.Max(-1, value);
            }
            else
            {
                Load().Limits.Remove(Key(userId));
            }

            Save();
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetMagnetAsync(Guid requestId, Guid episodeId, string magnetUrl, CancellationToken cancellationToken)
    {
        magnetUrl = magnetUrl.Trim();
        if (!magnetUrl.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || !magnetUrl.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("That isn't a magnet link (it should start with magnet:?xt=urn:btih:).", nameof(magnetUrl));
        }

        lock (_lock)
        {
            var requested = Load().Requests.FirstOrDefault(r => r.Id.Equals(requestId))?.Episodes.FirstOrDefault(e => e.EpisodeId.Equals(episodeId));
            if (requested is null)
            {
                return false;
            }

            requested.MagnetUrl = magnetUrl;
            Save();
        }

        if (_libraryManager.GetItemById(episodeId) is not Episode episode)
        {
            return false;
        }

        episode.DownloadUrl = magnetUrl;
        episode.IsDownloadable = true;
        episode.SourceCandidates = [];
        episode.NextSourceSearchUtc = null;
        await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        _downloadManager.QueueDownload(episode);
        _logger.LogInformation("An admin found a release for {Series} {Episode}", episode.SeriesName, episode.Name);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> CloseAsync(Guid requestId, DownloadRequestStatus status, string? note, CancellationToken cancellationToken)
    {
        if (status is not (DownloadRequestStatus.Fulfilled or DownloadRequestStatus.NotFound))
        {
            throw new ArgumentException("A request is closed as fulfilled or not found.", nameof(status));
        }

        DownloadRequest? request;
        lock (_lock)
        {
            request = Load().Requests.FirstOrDefault(r => r.Id.Equals(requestId) && r.Status == DownloadRequestStatus.Open);
            if (request is null)
            {
                return false;
            }

            request.Status = status;
            request.AdminNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            request.ClosedUtc = _timeProvider.GetUtcNow().UtcDateTime;
            Save();
        }

        _inbox.Add(
            request.UserId,
            UserNotificationKind.RequestClosed,
            GetClosedSubject(request),
            string.IsNullOrEmpty(request.AdminNote) ? GetOutcome(request) : $"{GetOutcome(request)} Note from the admin: {request.AdminNote}",
            request.ItemId);
        await EmailRequesterAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public bool Cancel(Guid requestId, Guid userId)
    {
        lock (_lock)
        {
            var request = Load().Requests.FirstOrDefault(r => r.Id.Equals(requestId) && r.UserId.Equals(userId) && r.Status == DownloadRequestStatus.Open);
            if (request is null)
            {
                return false;
            }

            request.Status = DownloadRequestStatus.Cancelled;
            request.ClosedUtc = _timeProvider.GetUtcNow().UtcDateTime;
            Save();
            return true;
        }
    }

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static string Label(Episode episode)
    {
        var number = string.Create(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00}");
        return string.IsNullOrWhiteSpace(episode.Name) ? number : $"{number}: {episode.Name}";
    }

    // An episode PirateFin gave up finding a release for.
    private bool IsMissing(Episode episode)
        => ReleaseStatusResolver.Resolve(episode, _timeProvider.GetUtcNow().UtcDateTime) == ReleaseStatus.NotFound;

    private List<Episode> GetMissingEpisodes(Season season)
        => _libraryManager.GetItemList(new InternalItemsQuery { ParentId = season.Id, IncludeItemTypes = [BaseItemKind.Episode] })
            .OfType<Episode>()
            .Where(IsMissing)
            .OrderBy(e => e.IndexNumber)
            .ToList();

    private async Task EmailAdminsAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        if (!_emailSender.IsConfigured)
        {
            return;
        }

        var poster = await _assets.GetPosterAsync(_libraryManager.GetItemById(request.ItemId)).ConfigureAwait(false);
        var message = new EmailBuilder($"Download request: {request.SeriesName} {request.Title}", $"{request.UserName} asked for episodes PirateFin couldn't find.")
            .Poster(poster)
            .Label("Download request")
            .Title(request.SeriesName)
            .Meta($"Asked for by {request.UserName}")
            .Text("PirateFin couldn't find a release for these episodes (none of the release groups, and no seeded fallback):")
            .EpisodeList(request.Episodes.Select(e => SplitLabel(e.Label)))
            .Text("Find a release, paste its magnet link for each episode, and close the request.")
            .Button("Open Requests", _assets.GetLink("dashboard/piratefin/requests"))
            .Footer("You get this email because you're a PirateFin admin.")
            .Build();

        foreach (var admin in _userManager.GetUsers().Where(u => u.HasPermission(PermissionKind.IsAdministrator)))
        {
            var email = _notificationSettings.Get(admin.Id).Email;
            if (string.IsNullOrWhiteSpace(email))
            {
                continue;
            }

            try
            {
                await _emailSender.SendAsync(email, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not email {Admin} about a request", admin.Username);
            }
        }
    }

    private static string GetOutcome(DownloadRequest request) => request.Status == DownloadRequestStatus.Fulfilled
        ? "An admin found it. It's downloading (or already downloaded) and will be ready to watch soon."
        : "An admin looked, but couldn't find a release for it either.";

    // "S01E05: Name" as its number and name.
    private static (string Number, string Name) SplitLabel(string label)
    {
        var colon = label.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? (label, string.Empty) : (label[..colon], label[(colon + 2)..]);
    }

    private static string GetRequestSubtitle(DownloadRequest request)
    {
        var (number, name) = SplitLabel(request.Title);
        return name.Length == 0 ? number : $"{number} · {name}";
    }

    private static string GetClosedSubject(DownloadRequest request) => request.Status == DownloadRequestStatus.Fulfilled
        ? $"Found: {request.SeriesName} {request.Title}"
        : $"Not found: {request.SeriesName} {request.Title}";

    private async Task EmailRequesterAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        var email = _notificationSettings.Get(request.UserId).Email;
        if (!_emailSender.IsConfigured || string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var found = request.Status == DownloadRequestStatus.Fulfilled;
        var poster = await _assets.GetPosterAsync(_libraryManager.GetItemById(request.ItemId)).ConfigureAwait(false);
        var message = new EmailBuilder(GetClosedSubject(request), $"Your request for {request.SeriesName} {request.Title} is closed.")
            .Greeting(request.UserName)
            .Poster(poster)
            .Label(found ? "Request found" : "Request closed", found ? EmailBuilder.Green : EmailBuilder.Muted)
            .Title(request.SeriesName)
            .Subtitle(GetRequestSubtitle(request))
            .Text(GetOutcome(request));
        if (!string.IsNullOrEmpty(request.AdminNote))
        {
            message.Note("Note from the admin:", request.AdminNote);
        }

        if (found)
        {
            message.Button("Open in PirateFin", _assets.GetItemLink(request.ItemId));
        }

        try
        {
            await _emailSender.SendAsync(email, message.Footer("You get this email because you asked for these episodes.").Build(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not email {User} about their request", request.UserName);
        }
    }

    private StoreData Load()
    {
        if (_data is not null)
        {
            return _data;
        }

        try
        {
            _data = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), _jsonOptions) ?? new StoreData()
                : new StoreData();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; starting with no requests", FilePath);
            _data = new StoreData();
        }

        return _data;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
        File.Move(temp, FilePath, true);
    }

    private sealed class StoreData
    {
        public int DefaultLimit { get; set; } = DefaultRequestLimit;

        public Dictionary<string, int> Limits { get; set; } = [];

        public List<DownloadRequest> Requests { get; set; } = [];
    }
}
