using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// The users' email settings, saved in &lt;data&gt;/piratefin/user-notifications.json.
/// </summary>
public sealed class UserNotificationSettingsStore : IUserNotificationSettings
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<UserNotificationSettingsStore> _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, NotificationSettings>? _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserNotificationSettingsStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public UserNotificationSettingsStore(IApplicationPaths appPaths, ILogger<UserNotificationSettingsStore> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "user-notifications.json");

    /// <inheritdoc />
    public NotificationSettings Get(Guid userId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(Key(userId), out var settings)
                ? new NotificationSettings { Email = settings.Email, NewEpisodeEmails = settings.NewEpisodeEmails }
                : new NotificationSettings();
        }
    }

    /// <inheritdoc />
    public void Set(Guid userId, NotificationSettings settings)
    {
        lock (_lock)
        {
            Load()[Key(userId)] = new NotificationSettings
            {
                Email = string.IsNullOrWhiteSpace(settings.Email) ? null : settings.Email.Trim(),
                NewEpisodeEmails = settings.NewEpisodeEmails
            };

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
            File.Move(temp, FilePath, true);
        }
    }

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private Dictionary<string, NotificationSettings> Load()
    {
        if (_settings is not null)
        {
            return _settings;
        }

        try
        {
            _settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, NotificationSettings>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; starting without email settings", FilePath);
            _settings = [];
        }

        return _settings;
    }
}
