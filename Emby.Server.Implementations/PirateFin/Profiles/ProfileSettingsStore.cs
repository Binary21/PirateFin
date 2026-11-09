using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Profiles;

/// <summary>
/// <see cref="IProfileSettings"/> in &lt;data&gt;/piratefin/profiles.json. PINs are stored as salted hashes. A title
/// removed from Continue Watching comes back when the user plays it (any episode, for a series).
/// </summary>
public sealed class ProfileSettingsStore : IProfileSettings, IHostedService
{
    /// <summary>
    /// Wrong PINs in a row before checks are refused for <see cref="LockoutTime"/>.
    /// </summary>
    public const int MaxPinAttempts = 5;

    /// <summary>
    /// How long PIN checks are refused after too many wrong tries.
    /// </summary>
    public static readonly TimeSpan LockoutTime = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IApplicationPaths _appPaths;
    private readonly ISessionManager _sessionManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProfileSettingsStore> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (int Failures, DateTime LockedUntil)> _pinAttempts = [];
    private Dictionary<string, Profile>? _profiles;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileSettingsStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="sessionManager">The session manager, to notice titles being played again.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public ProfileSettingsStore(IApplicationPaths appPaths, ISessionManager sessionManager, TimeProvider timeProvider, ILogger<ProfileSettingsStore> logger)
    {
        _appPaths = appPaths;
        _sessionManager = sessionManager;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "profiles.json");

    /// <inheritdoc />
    public bool HasPin(Guid userId)
    {
        lock (_lock)
        {
            return Get(userId, false)?.PinHash is not null;
        }
    }

    /// <inheritdoc />
    public void SetPin(Guid userId, string pin)
    {
        if (pin is null || pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A PIN is 4 digits.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        lock (_lock)
        {
            var profile = Get(userId, true)!;
            profile.PinSalt = Convert.ToBase64String(salt);
            profile.PinHash = Convert.ToBase64String(Hash(pin, salt));
            _pinAttempts.Remove(userId);
            Save();
        }
    }

    /// <inheritdoc />
    public void RemovePin(Guid userId)
    {
        lock (_lock)
        {
            if (Get(userId, false) is { PinHash: not null } profile)
            {
                profile.PinHash = null;
                profile.PinSalt = null;
                Save();
            }

            _pinAttempts.Remove(userId);
        }
    }

    /// <inheritdoc />
    public bool? VerifyPin(Guid userId, string pin)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (Get(userId, false) is not { PinHash: { } hash, PinSalt: { } salt })
            {
                return true;
            }

            if (_pinAttempts.TryGetValue(userId, out var attempts) && attempts.LockedUntil > now)
            {
                return null;
            }

            if (CryptographicOperations.FixedTimeEquals(Hash(pin ?? string.Empty, Convert.FromBase64String(salt)), Convert.FromBase64String(hash)))
            {
                _pinAttempts.Remove(userId);
                return true;
            }

            var failures = attempts.Failures + 1;
            _pinAttempts[userId] = failures >= MaxPinAttempts ? (0, now + LockoutTime) : (failures, DateTime.MinValue);
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsKids(Guid userId)
    {
        lock (_lock)
        {
            return Get(userId, false)?.IsKids == true;
        }
    }

    /// <inheritdoc />
    public void SetKids(Guid userId, bool isKids)
    {
        lock (_lock)
        {
            Get(userId, true)!.IsKids = isKids;
            Save();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetHiddenFromContinueWatching(Guid userId)
    {
        lock (_lock)
        {
            return Get(userId, false)?.HiddenFromContinueWatching.Select(Guid.Parse).ToList() ?? [];
        }
    }

    /// <inheritdoc />
    public void HideFromContinueWatching(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            if (Get(userId, true)!.HiddenFromContinueWatching.Add(Key(itemId)))
            {
                Save();
            }
        }
    }

    /// <inheritdoc />
    public bool ShowInContinueWatching(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            if (Get(userId, false)?.HiddenFromContinueWatching.Remove(Key(itemId)) == true)
            {
                Save();
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Brings a played title back to Continue Watching: the movie, or the series of the episode.
    /// </summary>
    /// <param name="e">The playback start report.</param>
    internal void OnPlaybackStart(PlaybackProgressEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        var titleId = e.Item is Episode episode && !episode.SeriesId.Equals(Guid.Empty) ? episode.SeriesId : e.Item.Id;
        foreach (var user in e.Users)
        {
            if (ShowInContinueWatching(user.Id, titleId))
            {
                _logger.LogDebug("{Title} is back in {User}'s Continue Watching", e.Item.Name, user.Username);
            }
        }
    }

    private static byte[] Hash(string pin, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, 100_000, HashAlgorithmName.SHA256, 32);

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e) => OnPlaybackStart(e);

    // Callers hold _lock.
    private Profile? Get(Guid userId, bool create)
    {
        var profiles = Load();
        if (!profiles.TryGetValue(Key(userId), out var profile) && create)
        {
            profile = new Profile();
            profiles[Key(userId)] = profile;
        }

        return profile;
    }

    // Callers hold _lock.
    private Dictionary<string, Profile> Load()
    {
        if (_profiles is not null)
        {
            return _profiles;
        }

        try
        {
            _profiles = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Profile>>(File.ReadAllText(FilePath), _jsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "Failed to read {Path}", FilePath);
        }

        _profiles ??= [];
        return _profiles;
    }

    // Callers hold _lock.
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
        File.Move(temp, FilePath, true);
    }

    private sealed class Profile
    {
        public string? PinHash { get; set; }

        public string? PinSalt { get; set; }

        public bool IsKids { get; set; }

        public HashSet<string> HiddenFromContinueWatching { get; set; } = [];
    }
}
