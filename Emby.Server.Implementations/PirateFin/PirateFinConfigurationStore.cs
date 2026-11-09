using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Notifications;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin;

/// <summary>
/// Reads the PirateFin settings through the options monitors and writes them back to the json files in the server's
/// configuration directory. Those files are loaded with reloadOnChange, so saved settings apply without a restart.
/// </summary>
public sealed class PirateFinConfigurationStore : IPirateFinConfigurationStore
{
    /// <summary>
    /// The Trakt / AniList / YTS settings file.
    /// </summary>
    public const string ProvidersFile = "providers.json";

    /// <summary>
    /// The Jackett settings file.
    /// </summary>
    public const string JackettFile = "jackett.json";

    /// <summary>
    /// The qBittorrent settings file.
    /// </summary>
    public const string QBitTorrentFile = "qbittorrent.json";

    /// <summary>
    /// The Gmail account emails are sent from.
    /// </summary>
    public const string GmailFile = "gmail.json";

    /// <summary>
    /// The trailer download settings file.
    /// </summary>
    public const string TrailersFile = "trailers.json";

    /// <summary>
    /// The YouTube tab's settings file.
    /// </summary>
    public const string YouTubeFile = "youtube.json";

    // Nulls are left out so the configuration binder keeps its own defaults for them.
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly IApplicationPaths _appPaths;
    private readonly IOptionsMonitor<InternetProviderOptions> _providers;
    private readonly IOptionsMonitor<JackettOptions> _jackett;
    private readonly IOptionsMonitor<QBitTorrentOptions> _qBitTorrent;
    private readonly IOptionsMonitor<GmailOptions> _gmail;
    private readonly IOptionsMonitor<TrailerOptions> _trailers;
    private readonly IOptionsMonitor<YouTubeOptions> _youTube;
    private readonly ILogger<PirateFinConfigurationStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PirateFinConfigurationStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="providers">The provider options.</param>
    /// <param name="jackett">The Jackett options.</param>
    /// <param name="qBitTorrent">The qBittorrent options.</param>
    /// <param name="gmail">The Gmail options.</param>
    /// <param name="trailers">The trailer options.</param>
    /// <param name="youTube">The YouTube options.</param>
    /// <param name="logger">The logger.</param>
    public PirateFinConfigurationStore(
        IApplicationPaths appPaths,
        IOptionsMonitor<InternetProviderOptions> providers,
        IOptionsMonitor<JackettOptions> jackett,
        IOptionsMonitor<QBitTorrentOptions> qBitTorrent,
        IOptionsMonitor<GmailOptions> gmail,
        IOptionsMonitor<TrailerOptions> trailers,
        IOptionsMonitor<YouTubeOptions> youTube,
        ILogger<PirateFinConfigurationStore> logger)
    {
        _appPaths = appPaths;
        _providers = providers;
        _jackett = jackett;
        _qBitTorrent = qBitTorrent;
        _gmail = gmail;
        _trailers = trailers;
        _youTube = youTube;
        _logger = logger;
    }

    /// <summary>
    /// Gets the names of all PirateFin settings files.
    /// </summary>
    public static IReadOnlyList<string> Files { get; } = [ProvidersFile, JackettFile, QBitTorrentFile, GmailFile, TrailersFile, YouTubeFile];

    /// <inheritdoc />
    public InternetProviderOptions GetProviders() => _providers.CurrentValue;

    /// <inheritdoc />
    public JackettOptions GetJackett() => _jackett.CurrentValue;

    /// <inheritdoc />
    public QBitTorrentOptions GetQBitTorrent() => _qBitTorrent.CurrentValue;

    /// <inheritdoc />
    public Task SaveProvidersAsync(InternetProviderOptions options, CancellationToken cancellationToken)
        => WriteAsync(ProvidersFile, InternetProviderOptions.SectionName, options, cancellationToken);

    /// <inheritdoc />
    public Task SaveJackettAsync(JackettOptions options, CancellationToken cancellationToken)
        => WriteAsync(JackettFile, JackettOptions.SectionName, options, cancellationToken);

    /// <inheritdoc />
    public Task SaveQBitTorrentAsync(QBitTorrentOptions options, CancellationToken cancellationToken)
        => WriteAsync(QBitTorrentFile, QBitTorrentOptions.SectionName, options, cancellationToken);

    /// <inheritdoc />
    public GmailOptions GetGmail() => _gmail.CurrentValue;

    /// <inheritdoc />
    public TrailerOptions GetTrailers() => _trailers.CurrentValue;

    /// <inheritdoc />
    public YouTubeOptions GetYouTube() => _youTube.CurrentValue;

    /// <inheritdoc />
    public Task SaveYouTubeAsync(YouTubeOptions options, CancellationToken cancellationToken)
        => WriteAsync(YouTubeFile, YouTubeOptions.SectionName, options, cancellationToken);

    /// <inheritdoc />
    public Task SaveTrailersAsync(TrailerOptions options, CancellationToken cancellationToken)
        => WriteAsync(TrailersFile, TrailerOptions.SectionName, options, cancellationToken);

    /// <inheritdoc />
    public bool IsGmailAppPasswordReadable() => GmailAppPasswordProtector.TryUnprotect(_gmail.CurrentValue.AppPassword, out _);

    /// <inheritdoc />
    public Task SaveGmailAsync(string address, string fromName, string? appPassword, CancellationToken cancellationToken)
    {
        var options = new GmailOptions
        {
            Address = address,
            FromName = fromName,
            AppPassword = appPassword is null ? _gmail.CurrentValue.AppPassword
                : appPassword.Length == 0 ? string.Empty
                : GmailAppPasswordProtector.Protect(appPassword),
            PublicAddress = _gmail.CurrentValue.PublicAddress
        };
        return WriteAsync(GmailFile, GmailOptions.SectionName, options, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveEmailPublicAddressAsync(string publicAddress, CancellationToken cancellationToken)
    {
        var current = _gmail.CurrentValue;
        var options = new GmailOptions
        {
            Address = current.Address,
            FromName = current.FromName,
            AppPassword = current.AppPassword,
            PublicAddress = publicAddress
        };
        return WriteAsync(GmailFile, GmailOptions.SectionName, options, cancellationToken);
    }

    private async Task WriteAsync<T>(string fileName, string section, T options, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_appPaths.ConfigurationDirectoryPath, fileName);
        var json = JsonSerializer.Serialize(new Dictionary<string, T> { [section] = options }, _jsonOptions);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Write next to the file and swap it in, so the config watcher never reads a half written file.
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }

        _logger.LogInformation("Saved PirateFin settings to {Path}", path);
    }
}
