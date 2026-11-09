using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.YouTube;

/// <summary>
/// The official YouTube Data API v3 and Google's OAuth device flow (no scraping), counting the daily quota units used
/// (YouTube resets the quota at midnight Pacific time).
/// </summary>
public sealed class YouTubeApi
{
    /// <summary>
    /// The read-only YouTube permission asked for when signing in with Google.
    /// </summary>
    public const string Scope = "https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>
    /// The quota units a search costs.
    /// </summary>
    public const int SearchCost = 100;

    private const string ApiBase = "https://www.googleapis.com/youtube/v3/";
    private const string OAuthBase = "https://oauth2.googleapis.com/";

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<YouTubeApi> _logger;
    private readonly Lock _quotaLock = new();
    private QuotaDay? _quota;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeApi"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public YouTubeApi(IHttpClientFactory httpClientFactory, IApplicationPaths appPaths, ILogger<YouTubeApi> logger)
    {
        _httpClientFactory = httpClientFactory;
        _appPaths = appPaths;
        _logger = logger;
    }

    private string QuotaPath => Path.Combine(_appPaths.DataPath, "piratefin", "youtube", "quota.json");

    /// <summary>
    /// Gets the quota units used and searches made today.
    /// </summary>
    /// <returns>Units and searches.</returns>
    public (int Units, int Searches) GetUsage()
    {
        lock (_quotaLock)
        {
            var quota = LoadQuota();
            return (quota.Units, quota.Searches);
        }
    }

    /// <summary>
    /// Takes one search from today's allowance.
    /// </summary>
    /// <param name="cap">The searches allowed per day.</param>
    /// <returns><c>true</c> if a search is allowed.</returns>
    public bool TryTakeSearch(int cap)
    {
        lock (_quotaLock)
        {
            var quota = LoadQuota();
            if (quota.Searches >= cap)
            {
                return false;
            }

            quota.Searches++;
            SaveQuota();
            return true;
        }
    }

    /// <summary>
    /// Starts Google's device sign in: a code to enter at google.com/device.
    /// </summary>
    /// <param name="clientId">The OAuth client id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The device code (kept by the server), the user code, where to enter it, expiry and poll interval.</returns>
    public async Task<(string DeviceCode, YouTubeDeviceCode Code)> StartDeviceSignInAsync(string clientId, CancellationToken cancellationToken)
    {
        using var json = await PostFormAsync("device/code", new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = Scope }, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        return (
            root.GetProperty("device_code").GetString()!,
            new YouTubeDeviceCode
            {
                UserCode = root.GetProperty("user_code").GetString()!,
                VerificationUrl = (root.TryGetProperty("verification_url", out var url) ? url : root.GetProperty("verification_uri")).GetString()!,
                ExpiresInSeconds = root.GetProperty("expires_in").GetInt32(),
                IntervalSeconds = root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5
            });
    }

    /// <summary>
    /// Checks whether the user entered the device code.
    /// </summary>
    /// <param name="clientId">The OAuth client id.</param>
    /// <param name="clientSecret">The OAuth client secret.</param>
    /// <param name="deviceCode">The device code.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tokens once signed in; otherwise Google's state (authorization_pending, slow_down, access_denied, expired_token).</returns>
    public async Task<(string? RefreshToken, string? AccessToken, DateTime Expires, string? Error)> PollDeviceSignInAsync(string clientId, string clientSecret, string deviceCode, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
        };
        using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
            .PostAsync(new Uri(OAuthBase + "token"), new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            return (null, null, default, root.TryGetProperty("error", out var error) ? error.GetString() : "error");
        }

        return (
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.GetProperty("access_token").GetString(),
            DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60),
            null);
    }

    /// <summary>
    /// Gets a new access token from a refresh token.
    /// </summary>
    /// <param name="clientId">The OAuth client id.</param>
    /// <param name="clientSecret">The OAuth client secret.</param>
    /// <param name="refreshToken">The refresh token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The access token and when it expires.</returns>
    /// <exception cref="YouTubeApiException">Google refused (e.g. the user revoked access).</exception>
    public async Task<(string AccessToken, DateTime Expires)> RefreshAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken)
    {
        using var json = await PostFormAsync(
            "token",
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            },
            cancellationToken).ConfigureAwait(false);
        return (json.RootElement.GetProperty("access_token").GetString()!, DateTime.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32() - 60));
    }

    /// <summary>
    /// Revokes a refresh token at Google (unlinking).
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .PostAsync(new Uri(OAuthBase + "revoke"), new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Couldn't revoke a YouTube sign in at Google");
        }
    }

    /// <summary>
    /// Gets the channels a user subscribes to: their own (signed in) or a channel's public subscriptions.
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="channelId">The channel whose public subscriptions to read; <c>null</c> for the signed in user's.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subscribed channel ids.</returns>
    public async Task<List<string>> GetSubscriptionsAsync(YouTubeAuth auth, string? channelId, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? pageToken = null;
        do
        {
            var query = new Dictionary<string, string?> { ["part"] = "snippet", ["maxResults"] = "50", ["pageToken"] = pageToken };
            if (channelId is null)
            {
                query["mine"] = "true";
            }
            else
            {
                query["channelId"] = channelId;
            }

            using var json = await GetAsync("subscriptions", query, auth, 1, cancellationToken).ConfigureAwait(false);
            foreach (var item in Items(json))
            {
                if (item.GetProperty("snippet").GetProperty("resourceId").TryGetProperty("channelId", out var id))
                {
                    ids.Add(id.GetString()!);
                }
            }

            pageToken = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        }
        while (pageToken is not null && ids.Count < 1000);

        return ids;
    }

    /// <summary>
    /// Gets channels' names, avatars, subscriber counts and uploads playlists (50 per call).
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="channelIds">The channel ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channels found.</returns>
    public Task<List<YouTubeChannel>> GetChannelsAsync(YouTubeAuth auth, IEnumerable<string> channelIds, CancellationToken cancellationToken)
        => GetChannelsAsync(auth, channelIds, false, cancellationToken);

    /// <summary>
    /// Gets channels' details (50 per call), with the banner and description for a channel's page.
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="channelIds">The channel ids.</param>
    /// <param name="withPage">Whether to include the banner and description.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channels found.</returns>
    public async Task<List<YouTubeChannel>> GetChannelsAsync(YouTubeAuth auth, IEnumerable<string> channelIds, bool withPage, CancellationToken cancellationToken)
    {
        var parts = withPage ? "snippet,statistics,contentDetails,brandingSettings" : "snippet,statistics,contentDetails";
        var channels = new List<YouTubeChannel>();
        foreach (var batch in channelIds.Distinct().Chunk(50))
        {
            using var json = await GetAsync("channels", new Dictionary<string, string?> { ["part"] = parts, ["id"] = string.Join(',', batch), ["maxResults"] = "50" }, auth, 1, cancellationToken).ConfigureAwait(false);
            channels.AddRange(Items(json).Select(ParseChannel));
        }

        return channels;
    }

    /// <summary>
    /// Gets the signed in user's own channel.
    /// </summary>
    /// <param name="auth">The sign in (an access token).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel, or <c>null</c> when the account has none.</returns>
    public async Task<YouTubeChannel?> GetMyChannelAsync(YouTubeAuth auth, CancellationToken cancellationToken)
    {
        using var json = await GetAsync("channels", new Dictionary<string, string?> { ["part"] = "snippet,statistics,contentDetails", ["mine"] = "true" }, auth, 1, cancellationToken).ConfigureAwait(false);
        return Items(json).Select(ParseChannel).FirstOrDefault();
    }

    /// <summary>
    /// Finds a channel by its @handle.
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="handle">The handle, with or without @.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channel, or <c>null</c>.</returns>
    public async Task<YouTubeChannel?> GetChannelByHandleAsync(YouTubeAuth auth, string handle, CancellationToken cancellationToken)
    {
        using var json = await GetAsync("channels", new Dictionary<string, string?> { ["part"] = "snippet,statistics,contentDetails", ["forHandle"] = handle.TrimStart('@') }, auth, 1, cancellationToken).ConfigureAwait(false);
        return Items(json).Select(ParseChannel).FirstOrDefault();
    }

    /// <summary>
    /// Gets the newest video ids of a playlist (a channel's uploads).
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="max">The most to get (up to 50).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The video ids, newest first.</returns>
    public async Task<List<string>> GetPlaylistVideoIdsAsync(YouTubeAuth auth, string playlistId, int max, CancellationToken cancellationToken)
        => (await GetPlaylistPageAsync(auth, playlistId, max, null, cancellationToken).ConfigureAwait(false)).VideoIds;

    /// <summary>
    /// Gets a page of a playlist's video ids (a channel's uploads, newest first).
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="max">The most to get (up to 50).</param>
    /// <param name="pageToken">The page; <c>null</c> for the first.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The video ids and the next page's token.</returns>
    public async Task<(List<string> VideoIds, string? NextPageToken)> GetPlaylistPageAsync(YouTubeAuth auth, string playlistId, int max, string? pageToken, CancellationToken cancellationToken)
    {
        using var json = await GetAsync(
            "playlistItems",
            new Dictionary<string, string?>
            {
                ["part"] = "contentDetails",
                ["playlistId"] = playlistId,
                ["maxResults"] = Math.Clamp(max, 1, 50).ToString(CultureInfo.InvariantCulture),
                ["pageToken"] = pageToken
            },
            auth,
            1,
            cancellationToken).ConfigureAwait(false);
        var ids = Items(json)
            .Select(i => i.GetProperty("contentDetails").TryGetProperty("videoId", out var id) ? id.GetString() : null)
            .OfType<string>()
            .ToList();
        return (ids, GetString(json.RootElement, "nextPageToken"));
    }

    /// <summary>
    /// Gets videos' details: title, description, thumbnails, length, views, category, live and made-for-kids flags.
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="videoIds">The video ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The videos found.</returns>
    public async Task<List<YouTubeVideo>> GetVideosAsync(YouTubeAuth auth, IEnumerable<string> videoIds, CancellationToken cancellationToken)
    {
        var videos = new List<YouTubeVideo>();
        foreach (var batch in videoIds.Distinct().Chunk(50))
        {
            using var json = await GetAsync("videos", new Dictionary<string, string?> { ["part"] = "snippet,contentDetails,statistics,status", ["id"] = string.Join(',', batch), ["maxResults"] = "50" }, auth, 1, cancellationToken).ConfigureAwait(false);
            videos.AddRange(Items(json).Select(ParseVideo));
        }

        return videos;
    }

    /// <summary>
    /// Gets the channels a channel features on its page (its "Featured channels" sections).
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="channelId">The channel id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The featured channel ids.</returns>
    public async Task<List<string>> GetFeaturedChannelsAsync(YouTubeAuth auth, string channelId, CancellationToken cancellationToken)
    {
        using var json = await GetAsync("channelSections", new Dictionary<string, string?> { ["part"] = "contentDetails", ["channelId"] = channelId }, auth, 1, cancellationToken).ConfigureAwait(false);
        return Items(json)
            .Where(s => s.TryGetProperty("contentDetails", out var details) && details.TryGetProperty("channels", out _))
            .SelectMany(s => s.GetProperty("contentDetails").GetProperty("channels").EnumerateArray().Select(c => c.GetString()))
            .OfType<string>()
            .Where(id => !string.Equals(id, channelId, StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Gets YouTube's most popular videos in a category.
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="categoryId">The category id.</param>
    /// <param name="regionCode">The region, e.g. US.</param>
    /// <param name="max">The most to get.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The videos.</returns>
    public async Task<List<YouTubeVideo>> GetMostPopularAsync(YouTubeAuth auth, string categoryId, string regionCode, int max, CancellationToken cancellationToken)
    {
        using var json = await GetAsync(
            "videos",
            new Dictionary<string, string?>
            {
                ["part"] = "snippet,contentDetails,statistics,status",
                ["chart"] = "mostPopular",
                ["videoCategoryId"] = categoryId,
                ["regionCode"] = regionCode,
                ["maxResults"] = Math.Clamp(max, 1, 50).ToString(CultureInfo.InvariantCulture)
            },
            auth,
            1,
            cancellationToken).ConfigureAwait(false);
        return Items(json).Select(ParseVideo).ToList();
    }

    /// <summary>
    /// Searches YouTube for videos, newest first (100 quota units).
    /// </summary>
    /// <param name="auth">The sign in.</param>
    /// <param name="query">The search text.</param>
    /// <param name="safeSearch">Whether to filter strictly (kids profiles).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The video ids found.</returns>
    public async Task<List<string>> SearchVideoIdsAsync(YouTubeAuth auth, string query, bool safeSearch, CancellationToken cancellationToken)
    {
        using var json = await GetAsync(
            "search",
            new Dictionary<string, string?>
            {
                ["part"] = "snippet",
                ["type"] = "video",
                ["q"] = query,
                ["maxResults"] = "25",
                ["order"] = "date",
                ["safeSearch"] = safeSearch ? "strict" : "moderate"
            },
            auth,
            SearchCost,
            cancellationToken).ConfigureAwait(false);
        return Items(json)
            .Select(i => i.GetProperty("id").TryGetProperty("videoId", out var id) ? id.GetString() : null)
            .OfType<string>()
            .ToList();
    }

    private static IEnumerable<JsonElement> Items(JsonDocument json)
        => json.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray() : [];

    private static string? BestThumbnail(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbnails))
        {
            return null;
        }

        foreach (var size in new[] { "maxres", "standard", "high", "medium", "default" })
        {
            if (thumbnails.TryGetProperty(size, out var thumbnail) && thumbnail.TryGetProperty("url", out var url))
            {
                return url.GetString();
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && long.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var number) ? number : null;

    private static YouTubeChannel ParseChannel(JsonElement item)
    {
        var snippet = item.GetProperty("snippet");
        var statistics = item.TryGetProperty("statistics", out var stats) ? stats : default;
        var hidden = statistics.ValueKind == JsonValueKind.Object && statistics.TryGetProperty("hiddenSubscriberCount", out var h) && h.GetBoolean();
        var hasPage = item.TryGetProperty("brandingSettings", out var branding);
        var banner = hasPage && branding.TryGetProperty("image", out var image) ? GetString(image, "bannerExternalUrl") : null;
        return new YouTubeChannel
        {
            Id = item.GetProperty("id").GetString()!,
            Title = GetString(snippet, "title") ?? string.Empty,
            AvatarUrl = BestThumbnail(snippet),
            SubscriberCount = statistics.ValueKind == JsonValueKind.Object && !hidden ? GetLong(statistics, "subscriberCount") : null,
            UploadsPlaylistId = item.TryGetProperty("contentDetails", out var details)
                && details.TryGetProperty("relatedPlaylists", out var playlists) ? GetString(playlists, "uploads") : null,
            Handle = GetString(snippet, "customUrl"),
            Description = hasPage ? GetString(snippet, "description") : null,

            // The banner's TV-safe middle, at a size for wide screens (the same crop YouTube's own page asks for).
            BannerUrl = banner is null ? null : banner + "=w2120-fcrop64=1,00005a57ffffa5a8-k-c0xffffffff-no-nd-rj",
            VideoCount = statistics.ValueKind == JsonValueKind.Object ? GetLong(statistics, "videoCount") : null
        };
    }

    private static YouTubeVideo ParseVideo(JsonElement item)
    {
        var snippet = item.GetProperty("snippet");
        var details = item.TryGetProperty("contentDetails", out var d) ? d : default;
        var status = item.TryGetProperty("status", out var s) ? s : default;
        var statistics = item.TryGetProperty("statistics", out var st) ? st : default;
        return new YouTubeVideo
        {
            Id = item.GetProperty("id").GetString()!,
            Title = GetString(snippet, "title") ?? string.Empty,
            Description = GetString(snippet, "description") ?? string.Empty,
            ChannelId = GetString(snippet, "channelId") ?? string.Empty,
            ChannelTitle = GetString(snippet, "channelTitle") ?? string.Empty,
            ThumbnailUrl = BestThumbnail(snippet),
            PublishedUtc = DateTime.TryParse(GetString(snippet, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var published) ? published : DateTime.MinValue,
            DurationSeconds = details.ValueKind == JsonValueKind.Object ? YouTubeParsing.ParseDurationSeconds(GetString(details, "duration")) : 0,
            ViewCount = statistics.ValueKind == JsonValueKind.Object ? GetLong(statistics, "viewCount") : null,
            CategoryId = GetString(snippet, "categoryId"),
            MadeForKids = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("madeForKids", out var kids) && kids.GetBoolean(),
            IsLive = !string.Equals(GetString(snippet, "liveBroadcastContent") ?? "none", "none", StringComparison.Ordinal)
        };
    }

    private async Task<JsonDocument> GetAsync(string resource, Dictionary<string, string?> query, YouTubeAuth auth, int cost, CancellationToken cancellationToken)
    {
        if (auth.AccessToken is null && !string.IsNullOrEmpty(auth.ApiKey))
        {
            query["key"] = auth.ApiKey;
        }

        var url = ApiBase + resource + "?" + string.Join('&', query.Where(q => q.Value is not null).Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value!)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth.AccessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        }

        AddUnits(cost);
        using var response = await _httpClientFactory.CreateClient(NamedClient.Default).SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (json)
            {
                throw ToException(json);
            }
        }

        return json;
    }

    private async Task<JsonDocument> PostFormAsync(string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
            .PostAsync(new Uri(OAuthBase + endpoint), new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using (json)
            {
                throw ToException(json);
            }
        }

        return json;
    }

    private static YouTubeApiException ToException(JsonDocument json)
    {
        var root = json.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                // OAuth errors: { "error": "invalid_grant", "error_description": "..." }
                return new YouTubeApiException(error.GetString()!, GetString(root, "error_description") ?? error.GetString()!);
            }

            var reason = error.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 ? GetString(errors[0], "reason") : null;
            return new YouTubeApiException(reason ?? "error", GetString(error, "message") ?? "YouTube refused the request.");
        }

        return new YouTubeApiException("error", "YouTube refused the request.");
    }

    private static string PacificDay()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void AddUnits(int cost)
    {
        lock (_quotaLock)
        {
            LoadQuota().Units += cost;
            SaveQuota();
        }
    }

    // Callers hold _quotaLock. A new Pacific day starts from zero.
    private QuotaDay LoadQuota()
    {
        var today = PacificDay();
        if (_quota is null)
        {
            try
            {
                _quota = File.Exists(QuotaPath) ? JsonSerializer.Deserialize<QuotaDay>(File.ReadAllText(QuotaPath), _jsonOptions) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Couldn't read {Path}", QuotaPath);
            }
        }

        if (_quota is null || !string.Equals(_quota.Day, today, StringComparison.Ordinal))
        {
            _quota = new QuotaDay { Day = today };
        }

        return _quota;
    }

    // Callers hold _quotaLock.
    private void SaveQuota()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QuotaPath)!);
        File.WriteAllText(QuotaPath, JsonSerializer.Serialize(_quota, _jsonOptions));
    }

    private sealed class QuotaDay
    {
        public string Day { get; set; } = string.Empty;

        public int Units { get; set; }

        public int Searches { get; set; }
    }
}
