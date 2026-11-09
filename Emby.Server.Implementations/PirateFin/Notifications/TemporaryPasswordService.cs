using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// <see cref="ITemporaryPasswords"/>, kept in &lt;data&gt;/piratefin/temporary-passwords.json. Only a salted hash of
/// each temporary password is stored.
/// </summary>
public sealed class TemporaryPasswordService : ITemporaryPasswords
{
    /// <summary>
    /// How long a temporary password works.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    // Asking again within this time doesn't send another email (so the form can't be used to flood an inbox).
    private static readonly TimeSpan _resendAfter = TimeSpan.FromMinutes(2);

    private const int HashIterations = 100_000;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IUserManager _userManager;
    private readonly IUserNotificationSettings _settings;
    private readonly IEmailSender _emailSender;
    private readonly IEmailAssets _assets;
    private readonly IApplicationPaths _appPaths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TemporaryPasswordService> _logger;
    private readonly Lock _lock = new();
    private StoreData? _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporaryPasswordService"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="settings">The users' email addresses.</param>
    /// <param name="emailSender">The email sender.</param>
    /// <param name="assets">The emails' links.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public TemporaryPasswordService(
        IUserManager userManager,
        IUserNotificationSettings settings,
        IEmailSender emailSender,
        IEmailAssets assets,
        IApplicationPaths appPaths,
        TimeProvider timeProvider,
        ILogger<TemporaryPasswordService> logger)
    {
        _userManager = userManager;
        _settings = settings;
        _emailSender = emailSender;
        _assets = assets;
        _appPaths = appPaths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "temporary-passwords.json");

    /// <inheritdoc />
    public async Task<bool> SendAsync(string username, CancellationToken cancellationToken)
    {
        var user = string.IsNullOrWhiteSpace(username) ? null : _userManager.GetUserByName(username.Trim());
        var email = user is null ? null : _settings.Get(user.Id).Email?.Trim();
        if (user is null || string.IsNullOrEmpty(email) || !_emailSender.IsConfigured)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var password = CreatePassword();
        lock (_lock)
        {
            var pending = Load().Pending;
            if (pending.TryGetValue(Key(user.Id), out var existing) && now - existing.CreatedUtc < _resendAfter)
            {
                return true;
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            pending[Key(user.Id)] = new PendingPassword
            {
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(Hash(password, salt)),
                CreatedUtc = now,
                ExpiresUtc = now + Lifetime
            };
            Save();
        }

        var minutes = string.Create(CultureInfo.InvariantCulture, $"{(int)Lifetime.TotalMinutes} minutes");
        var message = new EmailBuilder("Your temporary PirateFin password", $"Sign in with it within {minutes}.")
            .Greeting(user.Username)
            .Label("Temporary password")
            .Text("Here's your temporary password:")
            .PasswordBox(password)
            .Text($"Sign in with it within {minutes}. You'll then be asked to choose a new password.")
            .Text("If you didn't ask for this, ignore this email: your current password still works.", muted: true)
            .Button("Sign in", _assets.GetLink("login"))
            .Build();
        try
        {
            await _emailSender.SendAsync(email, message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not sent: forget it, so asking again sends a new one straight away.
            lock (_lock)
            {
                Load().Pending.Remove(Key(user.Id));
                Save();
            }

            throw;
        }

        _logger.LogInformation("Emailed a temporary password to {User}", user.Username);
        return true;
    }

    /// <inheritdoc />
    public bool TryRedeem(Guid userId, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            var data = Load();
            if (!data.Pending.TryGetValue(Key(userId), out var pending))
            {
                return false;
            }

            if (pending.ExpiresUtc <= now)
            {
                data.Pending.Remove(Key(userId));
                Save();
                return false;
            }

            // The dashes are optional, and case doesn't matter.
            var entered = Normalize(password);
            var expected = Convert.FromBase64String(pending.Hash);
            if (!CryptographicOperations.FixedTimeEquals(Hash(entered, Convert.FromBase64String(pending.Salt)), expected))
            {
                return false;
            }

            data.Pending.Remove(Key(userId));
            data.MustChange.Add(Key(userId));
            Save();
            return true;
        }
    }

    /// <inheritdoc />
    public bool IsChangeRequired(Guid userId)
    {
        lock (_lock)
        {
            return Load().MustChange.Contains(Key(userId));
        }
    }

    /// <inheritdoc />
    public void ClearChangeRequired(Guid userId)
    {
        lock (_lock)
        {
            if (Load().MustChange.Remove(Key(userId)))
            {
                Save();
            }
        }
    }

    /// <summary>
    /// Creates a temporary password like "3FA9-0C71-B2E4": easy to type, 48 random bits.
    /// </summary>
    /// <returns>The password.</returns>
    internal static string CreatePassword()
    {
        var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..]}";
    }

    private static string Normalize(string password)
        => password.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static byte[] Hash(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Normalize(password)), salt, HashIterations, HashAlgorithmName.SHA256, 32);

    private static string Key(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);

    // Callers hold _lock.
    private StoreData Load()
    {
        if (_data is not null)
        {
            return _data;
        }

        try
        {
            _data = File.Exists(FilePath) ? JsonSerializer.Deserialize<StoreData>(File.ReadAllText(FilePath), _jsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogError(ex, "Failed to read {Path}; starting with no temporary passwords", FilePath);
        }

        _data ??= new StoreData();
        return _data;
    }

    // Callers hold _lock.
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
        File.Move(temp, FilePath, true);
    }

    private sealed class StoreData
    {
        public Dictionary<string, PendingPassword> Pending { get; set; } = [];

        public HashSet<string> MustChange { get; set; } = [];
    }

    private sealed class PendingPassword
    {
        public string Salt { get; set; } = string.Empty;

        public string Hash { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }

        public DateTime ExpiresUtc { get; set; }
    }
}
