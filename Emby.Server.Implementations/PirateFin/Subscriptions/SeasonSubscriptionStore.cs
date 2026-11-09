using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// The users subscribed to each season, saved in &lt;data&gt;/piratefin/subscriptions.json. Keeps
/// <see cref="Season.Subscribed"/> (which the downloads use) true while anyone is subscribed.
/// </summary>
/// <remarks>
/// Subscriptions used to be one flag per season. On first start the seasons subscribed that way are given to the admin
/// accounts.
/// </remarks>
public sealed class SeasonSubscriptionStore : ISeasonSubscriptions, IHostedService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IApplicationPaths _appPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SeasonSubscriptionStore> _logger;
    private readonly Lock _lock = new();
    private StoreData? _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonSubscriptionStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public SeasonSubscriptionStore(
        IApplicationPaths appPaths,
        ILibraryManager libraryManager,
        IUserManager userManager,
        TimeProvider timeProvider,
        ILogger<SeasonSubscriptionStore> logger)
    {
        _appPaths = appPaths;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "subscriptions.json");

    /// <inheritdoc />
    public bool IsSubscribed(Guid seasonId, Guid userId)
    {
        lock (_lock)
        {
            return GetSeason(seasonId, false)?.Subscribers.ContainsKey(Key(userId)) == true;
        }
    }

    /// <inheritdoc />
    public bool HasUnsubscribed(Guid seasonId, Guid userId)
    {
        lock (_lock)
        {
            return GetSeason(seasonId, false)?.Unsubscribed.Contains(Key(userId)) == true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SeasonSubscriber> GetSubscribers(Guid seasonId)
    {
        lock (_lock)
        {
            var entry = GetSeason(seasonId, false);
            return entry?.Subscribers
                .Select(s => new SeasonSubscriber(Guid.Parse(s.Key), s.Value, entry.Languages.TryGetValue(s.Key, out var language) ? language : null))
                .ToList() ?? [];
        }
    }

    /// <inheritdoc />
    public ReleaseLanguage? GetLanguage(Guid seasonId, Guid userId)
    {
        lock (_lock)
        {
            var entry = GetSeason(seasonId, false);
            return entry is not null && entry.Subscribers.ContainsKey(Key(userId)) && entry.Languages.TryGetValue(Key(userId), out var language)
                ? language
                : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<(Guid SeasonId, DateTime SinceUtc)> GetSubscriptions(Guid userId)
    {
        var key = Key(userId);
        lock (_lock)
        {
            return Load().Seasons
                .Where(s => s.Value.Subscribers.ContainsKey(key))
                .Select(s => (Guid.Parse(s.Key), s.Value.Subscribers[key]))
                .ToList();
        }
    }

    /// <inheritdoc />
    public Task<bool> SubscribeAsync(Season season, Guid userId, CancellationToken cancellationToken)
        => SubscribeAsync(season, userId, null, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> SubscribeAsync(Season season, Guid userId, ReleaseLanguage? language, CancellationToken cancellationToken)
    {
        bool added;
        lock (_lock)
        {
            var entry = GetSeason(season.Id, true)!;
            entry.Unsubscribed.Remove(Key(userId));
            added = entry.Subscribers.TryAdd(Key(userId), _timeProvider.GetUtcNow().UtcDateTime);
            if (language.HasValue)
            {
                entry.Languages[Key(userId)] = language.Value;
            }

            Save();
        }

        if (!season.Subscribed)
        {
            season.Subscribed = true;
            await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync(Season season, Guid userId, CancellationToken cancellationToken)
    {
        bool anyoneLeft;
        lock (_lock)
        {
            var entry = GetSeason(season.Id, true)!;
            entry.Subscribers.Remove(Key(userId));
            entry.Languages.Remove(Key(userId));
            entry.Unsubscribed.Add(Key(userId));
            anyoneLeft = entry.Subscribers.Count > 0;
            Save();
        }

        if (season.Subscribed && !anyoneLeft)
        {
            season.Subscribed = false;
            await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            MigrateGlobalSubscriptions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not move the season subscriptions to the admin accounts");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    // Seasons subscribed before subscriptions were per user belong to the admins (from now on, so they're not told
    // about episodes that aired before).
    private void MigrateGlobalSubscriptions()
    {
        lock (_lock)
        {
            if (Load().MigratedGlobalSubscriptions)
            {
                return;
            }
        }

        var admins = _userManager.GetUsers().Where(u => u.HasPermission(PermissionKind.IsAdministrator)).Select(u => Key(u.Id)).ToList();
        var seasons = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Season], Recursive = true })
            .OfType<Season>()
            .Where(s => s.Subscribed)
            .ToList();

        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var season in seasons)
            {
                var entry = GetSeason(season.Id, true)!;
                foreach (var admin in admins)
                {
                    entry.Subscribers.TryAdd(admin, now);
                }
            }

            Load().MigratedGlobalSubscriptions = true;
            Save();
        }

        _logger.LogInformation("Gave {Count} subscribed seasons to the {Admins} admin accounts", seasons.Count, admins.Count);
    }

    private SeasonEntry? GetSeason(Guid seasonId, bool create)
    {
        var seasons = Load().Seasons;
        if (seasons.TryGetValue(Key(seasonId), out var entry))
        {
            return entry;
        }

        if (!create)
        {
            return null;
        }

        entry = new SeasonEntry();
        seasons[Key(seasonId)] = entry;
        return entry;
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
                ? JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath)) ?? new StoreData()
                : new StoreData();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; starting with no subscriptions", FilePath);
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
        public bool MigratedGlobalSubscriptions { get; set; }

        public Dictionary<string, SeasonEntry> Seasons { get; set; } = [];
    }

    private sealed class SeasonEntry
    {
        public Dictionary<string, DateTime> Subscribers { get; set; } = [];

        public HashSet<string> Unsubscribed { get; set; } = [];

        // The language each subscriber chose (anime). Missing: subscribed before languages existed.
        public Dictionary<string, ReleaseLanguage> Languages { get; set; } = [];
    }
}
