using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Security;
using Emby.Server.Implementations.PirateFin.Trailers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.YouTube;

/// <summary>
/// <see cref="IYouTube"/>. Data comes only from the official YouTube Data API; feeds are cached per user in
/// &lt;data&gt;/piratefin/youtube and refreshed every <see cref="RefreshEvery"/> in the background. Google sign in
/// tokens are encrypted (DPAPI). Downloads go one at a time to "PirateFin YouTube" on the library drive with the most
/// room, respecting the minimum free space.
/// </summary>
public sealed class YouTubeService : BackgroundService, IYouTube
{
    /// <summary>
    /// How often a linked user's feed is refreshed.
    /// </summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(2);

    /// <summary>
    /// The purpose of encrypted Google refresh tokens.
    /// </summary>
    public const string RefreshTokenPurpose = "YouTube.RefreshToken";

    /// <summary>
    /// The purpose of the encrypted OAuth client secret.
    /// </summary>
    public const string ClientSecretPurpose = "YouTube.ClientSecret";

    private const int UploadsPerChannel = 8;
    private const int RecommendedChannels = 12;
    private const int UploadsPerRecommendedChannel = 4;
    private const int PopularPerCategory = 8;
    private const int FeaturedChecksPerRefresh = 40;
    private const int MaxCachedVideos = 1500;
    private const double WatchedShare = 0.95;

    private static readonly TimeSpan _featuredFor = TimeSpan.FromDays(7);
    private static readonly TimeSpan _keepRecommended = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static readonly string _downloadFormat = "bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/b[height<=1080]";

    private readonly YouTubeApi _api;
    private readonly YouTubeStreams _streams;
    private readonly YtDlp _ytDlp;
    private readonly IOptionsMonitor<YouTubeOptions> _options;
    private readonly IProfileSettings _profiles;
    private readonly ILibraryStorage _storage;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<YouTubeService> _logger;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();
    private readonly ConcurrentDictionary<Guid, (string DeviceCode, DateTime Expires)> _pendingSignIns = new();
    private readonly ConcurrentDictionary<Guid, (string Token, DateTime Expires)> _accessTokens = new();
    private readonly Channel<string> _downloadQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private Dictionary<string, Account>? _accounts;
    private Dictionary<string, Dictionary<string, Progress>>? _progress;
    private Dictionary<string, Download>? _downloads;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeService"/> class.
    /// </summary>
    /// <param name="api">The YouTube Data API.</param>
    /// <param name="streams">The streams.</param>
    /// <param name="ytDlp">The yt-dlp tool, for downloads.</param>
    /// <param name="options">The YouTube options.</param>
    /// <param name="profiles">The profile settings (kids profiles).</param>
    /// <param name="storage">The library storage (where downloads go).</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public YouTubeService(
        YouTubeApi api,
        YouTubeStreams streams,
        YtDlp ytDlp,
        IOptionsMonitor<YouTubeOptions> options,
        IProfileSettings profiles,
        ILibraryStorage storage,
        IApplicationPaths appPaths,
        ILogger<YouTubeService> logger)
    {
        _api = api;
        _streams = streams;
        _ytDlp = ytDlp;
        _options = options;
        _profiles = profiles;
        _storage = storage;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string Folder => Path.Combine(_appPaths.DataPath, "piratefin", "youtube");

    private YouTubeOptions Options => _options.CurrentValue;

    /// <inheritdoc />
    public YouTubeStatus GetStatus(Guid userId)
    {
        var account = GetAccount(userId);
        var cache = account is null ? null : LoadCache(userId);
        return new YouTubeStatus
        {
            Enabled = Options.Enabled,
            Linked = account is not null,
            Mode = account?.Mode,
            ChannelTitle = account?.ChannelTitle,
            GoogleAvailable = !string.IsNullOrEmpty(Options.ClientId) && SecretProtector.TryUnprotect(Options.ClientSecret, ClientSecretPurpose, out _),
            ChannelIdAvailable = !string.IsNullOrEmpty(Options.ApiKey),
            LastRefreshUtc = cache?.LastRefreshUtc,
            LastError = cache?.LastError
        };
    }

    /// <inheritdoc />
    public async Task<YouTubeDeviceCode> StartGoogleLinkAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (clientId, _) = GetClient();
        try
        {
            var (deviceCode, code) = await _api.StartDeviceSignInAsync(clientId, cancellationToken).ConfigureAwait(false);
            _pendingSignIns[userId] = (deviceCode, DateTime.UtcNow.AddSeconds(code.ExpiresInSeconds));
            return code;
        }
        catch (YouTubeApiException ex)
        {
            throw new InvalidOperationException("Google refused to start the sign in: " + ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> PollGoogleLinkAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!_pendingSignIns.TryGetValue(userId, out var pending) || pending.Expires < DateTime.UtcNow)
        {
            _pendingSignIns.TryRemove(userId, out _);
            return "expired";
        }

        var (clientId, clientSecret) = GetClient();
        var result = await _api.PollDeviceSignInAsync(clientId, clientSecret, pending.DeviceCode, cancellationToken).ConfigureAwait(false);
        switch (result.Error)
        {
            case null:
                break;
            case "authorization_pending":
            case "slow_down":
                return "pending";
            case "access_denied":
                _pendingSignIns.TryRemove(userId, out _);
                return "denied";
            default:
                _pendingSignIns.TryRemove(userId, out _);
                return "expired";
        }

        _pendingSignIns.TryRemove(userId, out _);
        if (result.RefreshToken is null || result.AccessToken is null)
        {
            return "denied";
        }

        _accessTokens[userId] = (result.AccessToken, result.Expires);
        YouTubeChannel? channel = null;
        try
        {
            channel = await _api.GetMyChannelAsync(new YouTubeAuth(result.AccessToken, null), cancellationToken).ConfigureAwait(false);
        }
        catch (YouTubeApiException ex)
        {
            _logger.LogWarning(ex, "Couldn't read the signed in YouTube channel");
        }

        SaveAccount(userId, new Account
        {
            Mode = "Google",
            RefreshToken = SecretProtector.Protect(result.RefreshToken, RefreshTokenPurpose),
            ChannelId = channel?.Id,
            ChannelTitle = channel?.Title ?? "Your YouTube account"
        });
        _ = Task.Run(() => RefreshQuietlyAsync(userId, CancellationToken.None), CancellationToken.None);
        return "linked";
    }

    /// <inheritdoc />
    public async Task LinkChannelAsync(Guid userId, string channel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(Options.ApiKey))
        {
            throw new InvalidOperationException("Linking by channel isn't set up on this server yet. Ask your admin.");
        }

        var auth = new YouTubeAuth(null, Options.ApiKey);
        var (channelId, handle) = YouTubeParsing.ParseChannelInput(channel);
        YouTubeChannel? found = null;
        try
        {
            if (channelId is not null)
            {
                found = (await _api.GetChannelsAsync(auth, [channelId], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            }
            else if (handle is not null)
            {
                found = await _api.GetChannelByHandleAsync(auth, handle, cancellationToken).ConfigureAwait(false);
            }

            if (found is null)
            {
                throw new InvalidOperationException("No YouTube channel was found. Enter your channel id (UC…), @handle or channel link.");
            }

            // Checks the subscriptions can be read.
            await _api.GetSubscriptionsAsync(auth, found.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (YouTubeApiException ex) when (ex.Reason == "subscriptionForbidden")
        {
            throw new InvalidOperationException("Your subscriptions are private. On YouTube, open Settings → Privacy and turn off \"Keep all my subscriptions private\", then try again.", ex);
        }
        catch (YouTubeApiException ex)
        {
            throw new InvalidOperationException(DescribeError(ex), ex);
        }

        SaveAccount(userId, new Account { Mode = "Channel", ChannelId = found.Id, ChannelTitle = found.Title });
        _ = Task.Run(() => RefreshQuietlyAsync(userId, CancellationToken.None), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task UnlinkAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = GetAccount(userId);
        if (account?.RefreshToken is not null && SecretProtector.TryUnprotect(account.RefreshToken, RefreshTokenPurpose, out var token))
        {
            await _api.RevokeAsync(token, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            LoadAccounts().Remove(Key(userId));
            SaveJson(AccountsPath, _accounts);
        }

        _accessTokens.TryRemove(userId, out _);
        TryDelete(CachePath(userId));
    }

    /// <inheritdoc />
    public async Task RefreshAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = GetAccount(userId);
        if (account is null || !Options.Enabled)
        {
            return;
        }

        var gate = _refreshLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = LoadCache(userId) ?? new FeedCache();
            try
            {
                await RefreshCacheAsync(userId, account, cache, cancellationToken).ConfigureAwait(false);
                cache.LastError = null;
            }
            catch (YouTubeApiException ex)
            {
                cache.LastError = DescribeError(ex);
                _logger.LogWarning("Refreshing the YouTube feed of {UserId} failed: {Reason} {Message}", userId, ex.Reason, ex.Message);
                if (ex.Reason == "invalid_grant")
                {
                    // The sign in was removed at Google: the user has to link again.
                    await UnlinkAsync(userId, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            cache.LastRefreshUtc = DateTime.UtcNow;
            SaveJson(CachePath(userId), cache);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<YouTubeFeedPage> GetFeedAsync(Guid userId, int startIndex, int count, CancellationToken cancellationToken)
    {
        var cache = LoadCache(userId);
        if (cache is null || DateTime.UtcNow - cache.LastRefreshUtc > RefreshEvery)
        {
            await RefreshAsync(userId, cancellationToken).ConfigureAwait(false);
            cache = LoadCache(userId);
        }

        if (cache is null)
        {
            return new YouTubeFeedPage();
        }

        var feed = YouTubeFeedBuilder.Build(cache.Videos.Values.Select(v => WithUserState(userId, cache, v)), DateTime.UtcNow, _profiles.IsKids(userId));
        var page = feed.Skip(Math.Max(0, startIndex)).Take(Math.Clamp(count, 1, 100)).ToList();
        return new YouTubeFeedPage { Items = page, HasMore = startIndex + page.Count < feed.Count };
    }

    /// <inheritdoc />
    public async Task<YouTubeVideoDetails?> GetVideoAsync(Guid userId, string videoId, CancellationToken cancellationToken)
    {
        var cache = LoadCache(userId) ?? new FeedCache();
        var auth = await GetAuthAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!cache.Videos.TryGetValue(videoId, out var video))
        {
            video = (await _api.GetVideosAsync(auth, [videoId], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            if (video is null)
            {
                return null;
            }

            video.FromSubscription = cache.Subscribed.ContainsKey(video.ChannelId);
        }

        var kidsOnly = _profiles.IsKids(userId);
        if (kidsOnly && !video.MadeForKids)
        {
            return null;
        }

        if (!cache.Subscribed.TryGetValue(video.ChannelId, out var channel) && !cache.Recommended.TryGetValue(video.ChannelId, out channel))
        {
            channel = (await _api.GetChannelsAsync(auth, [video.ChannelId], cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? new YouTubeChannel { Id = video.ChannelId, Title = video.ChannelTitle };
        }

        // Up next: more from this creator first, then the rest of the home grid.
        var candidates = YouTubeFeedBuilder.Build(cache.Videos.Values.Select(v => WithUserState(userId, cache, v)), DateTime.UtcNow, kidsOnly)
            .Where(v => !string.Equals(v.Id, videoId, StringComparison.Ordinal))
            .ToList();
        var upNext = candidates.Where(v => string.Equals(v.ChannelId, video.ChannelId, StringComparison.Ordinal)).Take(4)
            .Concat(candidates.Where(v => !string.Equals(v.ChannelId, video.ChannelId, StringComparison.Ordinal)))
            .Take(15)
            .ToList();

        return new YouTubeVideoDetails { Video = WithUserState(userId, cache, video), Channel = channel, UpNext = upNext };
    }

    /// <inheritdoc />
    public async Task<YouTubeChannel?> GetChannelAsync(Guid userId, string channelId, CancellationToken cancellationToken)
    {
        var cache = LoadCache(userId) ?? new FeedCache();
        var auth = await GetAuthAsync(userId, cancellationToken).ConfigureAwait(false);
        var channel = (await _api.GetChannelsAsync(auth, [channelId], true, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (channel is not null)
        {
            channel.Subscribed = cache.Subscribed.ContainsKey(channel.Id);
        }

        return channel;
    }

    /// <inheritdoc />
    public async Task<YouTubeChannelVideos> GetChannelVideosAsync(Guid userId, string channelId, string? pageToken, CancellationToken cancellationToken)
    {
        var cache = LoadCache(userId) ?? new FeedCache();
        var kidsOnly = _profiles.IsKids(userId);
        var auth = await GetAuthAsync(userId, cancellationToken).ConfigureAwait(false);

        // A channel's uploads playlist is its id with UU in place of UC.
        var (ids, next) = await _api.GetPlaylistPageAsync(auth, YouTubeParsing.GetUploadsPlaylistId(channelId), 50, pageToken, cancellationToken).ConfigureAwait(false);
        var videos = await _api.GetVideosAsync(auth, ids, cancellationToken).ConfigureAwait(false);
        var avatars = await GetAvatarsAsync(auth, cache, [channelId], cancellationToken).ConfigureAwait(false);
        var items = videos
            .Where(v => !v.IsLive && !YouTubeParsing.IsShort(v) && (!kidsOnly || v.MadeForKids))
            .OrderByDescending(v => v.PublishedUtc)
            .Select(v =>
            {
                v.FromSubscription = cache.Subscribed.ContainsKey(v.ChannelId);
                v.ChannelAvatarUrl = avatars.GetValueOrDefault(v.ChannelId);
                return WithUserState(userId, cache, v);
            })
            .ToList();
        return new YouTubeChannelVideos { Items = items, NextPageToken = next };
    }

    /// <inheritdoc />
    public async Task<YouTubeSearchResult> SearchAsync(Guid userId, string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new YouTubeSearchResult { SearchesLeft = GetQuota().SearchesLeft };
        }

        if (!_api.TryTakeSearch(Options.DailySearchCap))
        {
            return new YouTubeSearchResult { Message = "Today's YouTube searches are used up. Searching works again tomorrow." };
        }

        var cache = LoadCache(userId) ?? new FeedCache();
        var kidsOnly = _profiles.IsKids(userId);
        try
        {
            var auth = await GetAuthAsync(userId, cancellationToken).ConfigureAwait(false);
            var channel = await FindChannelAsync(auth, cache, query, cancellationToken).ConfigureAwait(false);
            var ids = await _api.SearchVideoIdsAsync(auth, query.Trim(), kidsOnly, cancellationToken).ConfigureAwait(false);
            var videos = await _api.GetVideosAsync(auth, ids, cancellationToken).ConfigureAwait(false);
            var avatars = await GetAvatarsAsync(auth, cache, videos.Select(v => v.ChannelId), cancellationToken).ConfigureAwait(false);
            var items = videos
                .Where(v => !v.IsLive && !YouTubeParsing.IsShort(v) && (!kidsOnly || v.MadeForKids))
                .Select(v =>
                {
                    v.FromSubscription = cache.Subscribed.ContainsKey(v.ChannelId);
                    v.ChannelAvatarUrl = avatars.GetValueOrDefault(v.ChannelId);
                    return WithUserState(userId, cache, v);
                })
                .OrderByDescending(v => v.PublishedUtc)
                .ToList();
            return new YouTubeSearchResult { Channel = channel, Items = items, SearchesLeft = GetQuota().SearchesLeft };
        }
        catch (YouTubeApiException ex)
        {
            return new YouTubeSearchResult { Message = DescribeError(ex), SearchesLeft = GetQuota().SearchesLeft };
        }
    }

    /// <inheritdoc />
    public void SaveProgress(Guid userId, string videoId, double positionSeconds, double durationSeconds)
    {
        lock (_lock)
        {
            var progress = LoadProgress();
            if (!progress.TryGetValue(Key(userId), out var videos))
            {
                videos = [];
                progress[Key(userId)] = videos;
            }

            var watched = durationSeconds > 0 && (positionSeconds >= durationSeconds * WatchedShare || durationSeconds - positionSeconds <= 30);
            videos[videoId] = new Progress
            {
                PositionSeconds = watched ? 0 : Math.Max(0, positionSeconds),
                Watched = watched || (videos.TryGetValue(videoId, out var old) && old.Watched && positionSeconds < 5),
                LastUtc = DateTime.UtcNow
            };
            SaveJson(ProgressPath, progress);
        }
    }

    /// <inheritdoc />
    public async Task<string> StartStreamAsync(Guid userId, string videoId, CancellationToken cancellationToken)
    {
        if (!Options.Enabled)
        {
            throw new InvalidOperationException("YouTube is turned off on this server.");
        }

        if (_profiles.IsKids(userId))
        {
            var details = await GetVideoAsync(userId, videoId, cancellationToken).ConfigureAwait(false);
            if (details is null)
            {
                throw new InvalidOperationException("This video isn't made for kids.");
            }
        }

        string? file;
        lock (_lock)
        {
            file = LoadDownloads().TryGetValue(videoId, out var download) && download.Ready && File.Exists(download.Path) ? download.Path : null;
        }

        return await _streams.StartAsync(userId, videoId, file, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string?> GetStreamFileAsync(string token, string fileName, CancellationToken cancellationToken)
        => _streams.GetFileAsync(token, fileName, cancellationToken);

    /// <inheritdoc />
    public void QueueDownload(Guid userId, string videoId)
    {
        lock (_lock)
        {
            var downloads = LoadDownloads();
            if (!downloads.TryGetValue(videoId, out var download))
            {
                download = new Download();
                downloads[videoId] = download;
            }

            download.Users.Add(Key(userId));
            var start = !download.Ready && !download.Queued;
            download.Queued |= start;
            SaveJson(DownloadsPath, downloads);
            if (start)
            {
                _downloadQueue.Writer.TryWrite(videoId);
            }
        }
    }

    /// <inheritdoc />
    public void DeleteDownload(Guid userId, string videoId)
    {
        lock (_lock)
        {
            var downloads = LoadDownloads();
            if (!downloads.TryGetValue(videoId, out var download) || !download.Users.Remove(Key(userId)))
            {
                return;
            }

            // The file goes with its last user.
            if (download.Users.Count == 0)
            {
                downloads.Remove(videoId);
                if (download.Path is not null)
                {
                    TryDelete(download.Path);
                }
            }

            SaveJson(DownloadsPath, downloads);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool> GetDownloads(Guid userId)
    {
        lock (_lock)
        {
            return LoadDownloads()
                .Where(d => d.Value.Users.Contains(Key(userId)))
                .ToDictionary(d => d.Key, d => d.Value.Ready);
        }
    }

    /// <inheritdoc />
    public string ProtectClientSecret(string secret) => SecretProtector.Protect(secret, ClientSecretPurpose);

    /// <inheritdoc />
    public (int UnitsUsed, int SearchesLeft) GetQuota()
    {
        var (units, searches) = _api.GetUsage();
        return (units, Math.Max(0, Options.DailySearchCap - searches));
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (var gate in _refreshLocks.Values)
        {
            gate.Dispose();
        }

        base.Dispose();
    }

    /// <summary>
    /// Gets a description of a YouTube error for users.
    /// </summary>
    /// <param name="ex">The error.</param>
    /// <returns>The description.</returns>
    internal static string DescribeError(YouTubeApiException ex) => ex.Reason switch
    {
        "quotaExceeded" or "dailyLimitExceeded" => "YouTube's daily limit for this server is used up. It resets at midnight Pacific time.",
        "subscriptionForbidden" => "Your subscriptions are private. Turn off \"Keep all my subscriptions private\" in YouTube's privacy settings.",
        "invalid_grant" => "Your Google sign in expired or was removed. Link YouTube again.",
        "keyInvalid" or "API_KEY_INVALID" => "The server's YouTube API key isn't valid. Ask your admin.",
        "accessNotConfigured" => "The YouTube Data API isn't enabled in the server's Google Cloud project. Ask your admin.",
        _ => "YouTube refused the request: " + ex.Message
    };

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var downloads = ProcessDownloadsAsync(stoppingToken);

        // Re-queue downloads that were interrupted by a restart.
        lock (_lock)
        {
            foreach (var (videoId, _) in LoadDownloads().Where(d => d.Value.Queued && !d.Value.Ready))
            {
                _downloadQueue.Writer.TryWrite(videoId);
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            if (Options.Enabled)
            {
                List<Guid> users;
                lock (_lock)
                {
                    users = LoadAccounts().Keys.Select(Guid.Parse).ToList();
                }

                foreach (var userId in users.Where(u => LoadCache(u) is not { } c || DateTime.UtcNow - c.LastRefreshUtc > RefreshEvery))
                {
                    await RefreshQuietlyAsync(userId, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));

        await downloads.ConfigureAwait(false);
    }

    private static string Key(Guid userId) => userId.ToString("N", CultureInfo.InvariantCulture);

    private async Task RefreshQuietlyAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Refreshing the YouTube feed of {UserId} failed", userId);
        }
    }

    private async Task RefreshCacheAsync(Guid userId, Account account, FeedCache cache, CancellationToken cancellationToken)
    {
        var auth = await GetAuthAsync(userId, cancellationToken).ConfigureAwait(false);

        // Subscriptions and their channels.
        var subscriptionIds = await _api.GetSubscriptionsAsync(auth, account.Mode == "Channel" ? account.ChannelId : null, cancellationToken).ConfigureAwait(false);
        var channels = await _api.GetChannelsAsync(auth, subscriptionIds, cancellationToken).ConfigureAwait(false);
        cache.Subscribed = channels.ToDictionary(c => c.Id);

        // Their newest uploads.
        var uploadIds = await GetUploadsAsync(auth, channels, UploadsPerChannel, cancellationToken).ConfigureAwait(false);
        await AddVideosAsync(auth, cache, uploadIds, true, cancellationToken).ConfigureAwait(false);

        // Recommended creators: those most featured by the subscriptions' own channel pages.
        var due = cache.Subscribed.Keys
            .Where(id => !cache.Featured.TryGetValue(id, out var f) || DateTime.UtcNow - f.CheckedUtc > _featuredFor)
            .Take(FeaturedChecksPerRefresh)
            .ToList();
        foreach (var channelId in due)
        {
            try
            {
                cache.Featured[channelId] = new FeaturedChannels { CheckedUtc = DateTime.UtcNow, ChannelIds = await _api.GetFeaturedChannelsAsync(auth, channelId, cancellationToken).ConfigureAwait(false) };
            }
            catch (YouTubeApiException ex) when (ex.Reason != "quotaExceeded")
            {
                cache.Featured[channelId] = new FeaturedChannels { CheckedUtc = DateTime.UtcNow };
            }
        }

        foreach (var gone in cache.Featured.Keys.Where(id => !cache.Subscribed.ContainsKey(id)).ToList())
        {
            cache.Featured.Remove(gone);
        }

        var recommendedIds = cache.Featured.Values
            .SelectMany(f => f.ChannelIds)
            .Where(id => !cache.Subscribed.ContainsKey(id))
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count())
            .Take(RecommendedChannels)
            .Select(g => g.Key)
            .ToList();
        var recommended = await _api.GetChannelsAsync(auth, recommendedIds, cancellationToken).ConfigureAwait(false);
        cache.Recommended = recommended.ToDictionary(c => c.Id);
        var recommendedUploads = await GetUploadsAsync(auth, recommended, UploadsPerRecommendedChannel, cancellationToken).ConfigureAwait(false);
        await AddVideosAsync(auth, cache, recommendedUploads, false, cancellationToken).ConfigureAwait(false);

        // Popular in the categories the subscriptions are in.
        var categories = cache.Videos.Values
            .Where(v => v.FromSubscription && v.CategoryId is not null)
            .GroupBy(v => v.CategoryId!)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => g.Key);
        foreach (var category in categories)
        {
            try
            {
                foreach (var video in await _api.GetMostPopularAsync(auth, category, Options.RegionCode, PopularPerCategory, cancellationToken).ConfigureAwait(false))
                {
                    video.FromSubscription = cache.Subscribed.ContainsKey(video.ChannelId);
                    cache.Videos.TryAdd(video.Id, video);
                }
            }
            catch (YouTubeApiException ex) when (ex.Reason != "quotaExceeded")
            {
                // Some categories have no chart.
                _logger.LogDebug("No popular chart for category {Category}: {Message}", category, ex.Message);
            }
        }

        // Channel avatars for recommended videos from other channels (e.g. popular charts).
        var avatars = await GetAvatarsAsync(auth, cache, cache.Videos.Values.Select(v => v.ChannelId), cancellationToken).ConfigureAwait(false);
        foreach (var video in cache.Videos.Values)
        {
            video.ChannelAvatarUrl = avatars.GetValueOrDefault(video.ChannelId) ?? video.ChannelAvatarUrl;
        }

        Prune(cache);
    }

    private async Task<List<string>> GetUploadsAsync(YouTubeAuth auth, IEnumerable<YouTubeChannel> channels, int perChannel, CancellationToken cancellationToken)
    {
        var ids = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            channels.Where(c => c.UploadsPlaylistId is not null),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (channel, ct) =>
            {
                try
                {
                    foreach (var id in await _api.GetPlaylistVideoIdsAsync(auth, channel.UploadsPlaylistId!, perChannel, ct).ConfigureAwait(false))
                    {
                        ids.Add(id);
                    }
                }
                catch (YouTubeApiException ex) when (ex.Reason is "playlistNotFound" or "notFound")
                {
                    // A channel without uploads.
                }
            }).ConfigureAwait(false);
        return ids.ToList();
    }

    private async Task AddVideosAsync(YouTubeAuth auth, FeedCache cache, List<string> ids, bool fromSubscription, CancellationToken cancellationToken)
    {
        var missing = ids.Where(id => !cache.Videos.ContainsKey(id)).ToList();
        foreach (var video in await _api.GetVideosAsync(auth, missing, cancellationToken).ConfigureAwait(false))
        {
            video.FromSubscription = fromSubscription || cache.Subscribed.ContainsKey(video.ChannelId);
            cache.Videos[video.Id] = video;
        }

        // Known videos can change sides (a creator was subscribed to since).
        foreach (var video in cache.Videos.Values)
        {
            video.FromSubscription = cache.Subscribed.ContainsKey(video.ChannelId);
        }
    }

    private async Task<Dictionary<string, string?>> GetAvatarsAsync(YouTubeAuth auth, FeedCache cache, IEnumerable<string> channelIds, CancellationToken cancellationToken)
    {
        var avatars = cache.Subscribed.Values.Concat(cache.Recommended.Values).Concat(cache.OtherChannels.Values)
            .DistinctBy(c => c.Id)
            .ToDictionary(c => c.Id, c => c.AvatarUrl);
        var unknown = channelIds.Where(id => !avatars.ContainsKey(id)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            foreach (var channel in await _api.GetChannelsAsync(auth, unknown, cancellationToken).ConfigureAwait(false))
            {
                cache.OtherChannels[channel.Id] = channel;
                avatars[channel.Id] = channel.AvatarUrl;
            }
        }

        return avatars;
    }

    // The account a search names: one PirateFin already knows (free), or else an exact @handle (1 unit).
    private async Task<YouTubeChannel?> FindChannelAsync(YouTubeAuth auth, FeedCache cache, string query, CancellationToken cancellationToken)
    {
        var known = cache.Subscribed.Values.Concat(cache.Recommended.Values).Concat(cache.OtherChannels.Values).DistinctBy(c => c.Id);
        var channel = YouTubeParsing.MatchChannel(known, query);
        if (channel is null)
        {
            var handle = YouTubeParsing.ToHandle(query);
            if (handle is not null)
            {
                try
                {
                    channel = await _api.GetChannelByHandleAsync(auth, handle, cancellationToken).ConfigureAwait(false);
                }
                catch (YouTubeApiException ex)
                {
                    _logger.LogDebug(ex, "No YouTube channel @{Handle}", handle);
                }
            }
        }

        if (channel is null)
        {
            return null;
        }

        return new YouTubeChannel
        {
            Id = channel.Id,
            Title = channel.Title,
            AvatarUrl = channel.AvatarUrl,
            Handle = channel.Handle,
            SubscriberCount = channel.SubscriberCount,
            VideoCount = channel.VideoCount,
            Subscribed = cache.Subscribed.ContainsKey(channel.Id)
        };
    }

    private static void Prune(FeedCache cache)
    {
        var keep = cache.Videos.Values
            .Where(v => v.FromSubscription || DateTime.UtcNow - v.PublishedUtc <= _keepRecommended)
            .GroupBy(v => v.ChannelId)
            .SelectMany(g => g.OrderByDescending(v => v.PublishedUtc).Take(30))
            .OrderByDescending(v => v.PublishedUtc)
            .Take(MaxCachedVideos)
            .ToDictionary(v => v.Id);
        cache.Videos = keep;
    }

    private YouTubeVideo WithUserState(Guid userId, FeedCache cache, YouTubeVideo video)
    {
        Progress? progress;
        bool downloaded;
        lock (_lock)
        {
            progress = LoadProgress().TryGetValue(Key(userId), out var videos) && videos.TryGetValue(video.Id, out var p) ? p : null;
            downloaded = LoadDownloads().TryGetValue(video.Id, out var d) && d.Users.Contains(Key(userId));
        }

        var channel = cache.Subscribed.GetValueOrDefault(video.ChannelId) ?? cache.Recommended.GetValueOrDefault(video.ChannelId) ?? cache.OtherChannels.GetValueOrDefault(video.ChannelId);
        return new YouTubeVideo
        {
            Id = video.Id,
            Title = video.Title,
            Description = video.Description,
            ChannelId = video.ChannelId,
            ChannelTitle = video.ChannelTitle,
            ChannelAvatarUrl = channel?.AvatarUrl ?? video.ChannelAvatarUrl,
            ThumbnailUrl = video.ThumbnailUrl,
            PublishedUtc = video.PublishedUtc,
            DurationSeconds = video.DurationSeconds,
            ViewCount = video.ViewCount,
            CategoryId = video.CategoryId,
            MadeForKids = video.MadeForKids,
            IsLive = video.IsLive,
            FromSubscription = video.FromSubscription,
            PositionSeconds = progress?.PositionSeconds ?? 0,
            Watched = progress?.Watched ?? false,
            Downloaded = downloaded
        };
    }

    private async Task<YouTubeAuth> GetAuthAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = GetAccount(userId);
        if (account?.Mode != "Google" || account.RefreshToken is null)
        {
            if (string.IsNullOrEmpty(Options.ApiKey))
            {
                throw new YouTubeApiException("keyInvalid", "No API key is set.");
            }

            return new YouTubeAuth(null, Options.ApiKey);
        }

        if (_accessTokens.TryGetValue(userId, out var cached) && cached.Expires > DateTime.UtcNow)
        {
            return new YouTubeAuth(cached.Token, null);
        }

        if (!SecretProtector.TryUnprotect(account.RefreshToken, RefreshTokenPurpose, out var refreshToken))
        {
            throw new YouTubeApiException("invalid_grant", "The saved sign in can't be read on this server.");
        }

        var (clientId, clientSecret) = GetClient();
        var (token, expires) = await _api.RefreshAccessTokenAsync(clientId, clientSecret, refreshToken, cancellationToken).ConfigureAwait(false);
        _accessTokens[userId] = (token, expires);
        return new YouTubeAuth(token, null);
    }

    private (string ClientId, string ClientSecret) GetClient()
    {
        if (string.IsNullOrEmpty(Options.ClientId) || !SecretProtector.TryUnprotect(Options.ClientSecret, ClientSecretPurpose, out var secret))
        {
            throw new InvalidOperationException("Sign in with Google isn't set up on this server yet. Ask your admin.");
        }

        return (Options.ClientId, secret);
    }

    private async Task ProcessDownloadsAsync(CancellationToken stoppingToken)
    {
        await foreach (var videoId in _downloadQueue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await DownloadAsync(videoId, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Downloading YouTube video {VideoId} failed", videoId);
                SetDownload(videoId, d => d.Queued = false);
            }
        }
    }

    private async Task DownloadAsync(string videoId, CancellationToken cancellationToken)
    {
        // The library drive with the most room above the minimum free space.
        var location = _storage.GetLocations().Where(l => !l.IsFull).MaxBy(l => l.FreeBytes);
        if (location is null)
        {
            _logger.LogWarning("Not downloading YouTube video {VideoId}: every library drive is below the minimum free space", videoId);
            SetDownload(videoId, d => d.Queued = false);
            return;
        }

        var folder = Path.Combine(Path.GetPathRoot(location.Path)!, "PirateFin YouTube");
        Directory.CreateDirectory(folder);
        var (exitCode, output) = await _ytDlp.RunAsync(
            ["--no-playlist", "--no-progress", "-f", _downloadFormat, "--merge-output-format", "mp4", "-o", Path.Combine(folder, videoId + ".%(ext)s"), "https://www.youtube.com/watch?v=" + videoId],
            TimeSpan.FromHours(2),
            cancellationToken).ConfigureAwait(false);

        var file = Path.Combine(folder, videoId + ".mp4");
        if (exitCode != 0 || !File.Exists(file))
        {
            _logger.LogWarning("Downloading YouTube video {VideoId} failed: {Output}", videoId, output.Trim());
            SetDownload(videoId, d => d.Queued = false);
            return;
        }

        SetDownload(videoId, d =>
        {
            d.Path = file;
            d.Ready = true;
            d.Queued = false;
        });
        _logger.LogInformation("Downloaded YouTube video {VideoId} to {Path}", videoId, file);
    }

    private void SetDownload(string videoId, Action<Download> change)
    {
        lock (_lock)
        {
            if (LoadDownloads().TryGetValue(videoId, out var download))
            {
                change(download);
                SaveJson(DownloadsPath, _downloads);
            }
        }
    }

    private string AccountsPath => Path.Combine(Folder, "accounts.json");

    private string ProgressPath => Path.Combine(Folder, "progress.json");

    private string DownloadsPath => Path.Combine(Folder, "downloads.json");

    private string CachePath(Guid userId) => Path.Combine(Folder, "feeds", Key(userId) + ".json");

    private Account? GetAccount(Guid userId)
    {
        lock (_lock)
        {
            return LoadAccounts().GetValueOrDefault(Key(userId));
        }
    }

    private void SaveAccount(Guid userId, Account account)
    {
        lock (_lock)
        {
            LoadAccounts()[Key(userId)] = account;
            SaveJson(AccountsPath, _accounts);
        }
    }

    // Callers hold _lock.
    private Dictionary<string, Account> LoadAccounts() => _accounts ??= LoadJson<Dictionary<string, Account>>(AccountsPath) ?? [];

    // Callers hold _lock.
    private Dictionary<string, Dictionary<string, Progress>> LoadProgress() => _progress ??= LoadJson<Dictionary<string, Dictionary<string, Progress>>>(ProgressPath) ?? [];

    // Callers hold _lock.
    private Dictionary<string, Download> LoadDownloads() => _downloads ??= LoadJson<Dictionary<string, Download>>(DownloadsPath) ?? [];

    private FeedCache? LoadCache(Guid userId) => LoadJson<FeedCache>(CachePath(userId));

    private T? LoadJson<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), _jsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Couldn't read {Path}", path);
            return null;
        }
    }

    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, _jsonOptions));
        File.Move(temp, path, true);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Couldn't delete {Path}", path);
        }
    }

    private sealed class Account
    {
        public string Mode { get; set; } = string.Empty;

        public string? RefreshToken { get; set; }

        public string? ChannelId { get; set; }

        public string? ChannelTitle { get; set; }
    }

    private sealed class FeedCache
    {
        public DateTime LastRefreshUtc { get; set; }

        public string? LastError { get; set; }

        public Dictionary<string, YouTubeChannel> Subscribed { get; set; } = [];

        public Dictionary<string, YouTubeChannel> Recommended { get; set; } = [];

        public Dictionary<string, YouTubeChannel> OtherChannels { get; set; } = [];

        public Dictionary<string, FeaturedChannels> Featured { get; set; } = [];

        public Dictionary<string, YouTubeVideo> Videos { get; set; } = [];
    }

    private sealed class FeaturedChannels
    {
        public DateTime CheckedUtc { get; set; }

        public List<string> ChannelIds { get; set; } = [];
    }

    private sealed class Progress
    {
        public double PositionSeconds { get; set; }

        public bool Watched { get; set; }

        public DateTime LastUtc { get; set; }
    }

    private sealed class Download
    {
        public HashSet<string> Users { get; set; } = [];

        public string? Path { get; set; }

        public bool Ready { get; set; }

        public bool Queued { get; set; }
    }
}
