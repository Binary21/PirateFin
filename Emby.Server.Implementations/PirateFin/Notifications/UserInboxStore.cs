using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// The users' bell notifications, saved in &lt;data&gt;/piratefin/inbox.json. Only the newest
/// <see cref="MaxPerUser"/> per user are kept.
/// </summary>
public sealed class UserInboxStore : IUserInbox
{
    /// <summary>
    /// The number of notifications kept per user.
    /// </summary>
    public const int MaxPerUser = 100;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly IApplicationPaths _appPaths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UserInboxStore> _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, List<UserNotification>>? _inboxes;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserInboxStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public UserInboxStore(IApplicationPaths appPaths, TimeProvider timeProvider, ILogger<UserInboxStore> logger)
    {
        _appPaths = appPaths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "inbox.json");

    /// <inheritdoc />
    public void Add(Guid userId, UserNotificationKind kind, string title, string? text, Guid? itemId)
    {
        lock (_lock)
        {
            var inbox = GetInbox(userId);
            inbox.Insert(0, new UserNotification
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Title = title,
                Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
                ItemId = itemId,
                CreatedUtc = _timeProvider.GetUtcNow().UtcDateTime
            });
            if (inbox.Count > MaxPerUser)
            {
                inbox.RemoveRange(MaxPerUser, inbox.Count - MaxPerUser);
            }

            Save();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UserNotification> Get(Guid userId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(Key(userId), out var inbox) ? inbox.ToList() : [];
        }
    }

    /// <inheritdoc />
    public void MarkAllSeen(Guid userId)
    {
        lock (_lock)
        {
            if (Load().TryGetValue(Key(userId), out var inbox) && inbox.Any(n => !n.Seen))
            {
                inbox.ForEach(n => n.Seen = true);
                Save();
            }
        }
    }

    /// <inheritdoc />
    public bool Delete(Guid userId, Guid notificationId)
    {
        lock (_lock)
        {
            if (!Load().TryGetValue(Key(userId), out var inbox) || inbox.RemoveAll(n => n.Id.Equals(notificationId)) == 0)
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <inheritdoc />
    public void Clear(Guid userId)
    {
        lock (_lock)
        {
            if (Load().Remove(Key(userId)))
            {
                Save();
            }
        }
    }

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private List<UserNotification> GetInbox(Guid userId)
    {
        var inboxes = Load();
        if (!inboxes.TryGetValue(Key(userId), out var inbox))
        {
            inbox = [];
            inboxes[Key(userId)] = inbox;
        }

        return inbox;
    }

    private Dictionary<string, List<UserNotification>> Load()
    {
        if (_inboxes is not null)
        {
            return _inboxes;
        }

        try
        {
            _inboxes = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<UserNotification>>>(File.ReadAllText(FilePath), _jsonOptions) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; starting with empty notifications", FilePath);
            _inboxes = [];
        }

        return _inboxes;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
        File.Move(temp, FilePath, true);
    }
}
