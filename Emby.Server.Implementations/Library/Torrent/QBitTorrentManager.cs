using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed class QBitTorrentManager : IDisposable
{
    private readonly HttpClient _client;
    private readonly ILogger<QBitTorrentManager> _logger;
    private readonly string _qbUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly bool _manageProcessLifecycle;
    private bool _isAuthenticated;

    private readonly string _qbittorrentExePath;

    public QBitTorrentManager(string qbUrl, string username, string password, string qbittorrentExePath, ILogger<QBitTorrentManager> logger)
        : this(qbUrl, username, password, qbittorrentExePath, logger, new HttpClientHandler { UseCookies = true }, manageProcessLifecycle: true)
    {
    }

    internal QBitTorrentManager(string qbUrl, string username, string password, string qbittorrentExePath, ILogger<QBitTorrentManager> logger, HttpMessageHandler httpMessageHandler, bool manageProcessLifecycle)
    {
        _qbUrl = qbUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _qbittorrentExePath = qbittorrentExePath;
        _client = new HttpClient(httpMessageHandler);
        _logger = logger;
        _manageProcessLifecycle = manageProcessLifecycle;

        _logger.LogInformation("Creating singleton instance");
    }

    private void EnsureQbittorrentAlive()
    {
        if (!_manageProcessLifecycle)
        {
            return;
        }

        var processes = Process.GetProcessesByName("qbittorrent");
        if (processes.Length == 0)
        {
            if (!File.Exists(_qbittorrentExePath))
            {
                _logger.LogWarning("qBittorrent executable not found at {Path}", _qbittorrentExePath);
                return;
            }

            _logger.LogInformation("qBittorrent application launching");
            Process.Start(new ProcessStartInfo
            {
                FileName = _qbittorrentExePath,
                UseShellExecute = true
            });

            Task.Delay(3000).Wait(); // wait a moment for qBittorrent to start
        }
    }

    public async Task EnsureLoginAsync(CancellationToken cancellationToken = default)
    {
        // Mullvlad connect async currently does not work so will be skipped
        // await EnsureMullvadConnectedAsync();
        EnsureQbittorrentAlive();

        if (_isAuthenticated)
        {
            return;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isAuthenticated)
            {
                return;
            }

            _logger.LogInformation("Attempting qBittorrent authentication");
            var loginData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", _username),
                new KeyValuePair<string, string>("password", _password)
            });

            var response = await _client.PostAsync($"{_qbUrl}/api/v2/auth/login", loginData, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || text.Contains("Fail", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("qBittorrent authentication failed: {Text}", text);
                return;
            }

            _isAuthenticated = true;
            _logger.LogInformation("qBittorrent authentication successful");
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> sender,
        string operationName,
        CancellationToken cancellationToken)
    {
        await EnsureLoginAsync(cancellationToken).ConfigureAwait(false);

        var response = await sender(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        if (response.StatusCode is not System.Net.HttpStatusCode.Unauthorized
            && response.StatusCode is not System.Net.HttpStatusCode.Forbidden)
        {
            return response;
        }

        response.Dispose();
        _logger.LogWarning(
            "qBittorrent auth expired during {Operation}; forcing re-authentication and retrying once",
            operationName);

        _isAuthenticated = false;
        await EnsureLoginAsync(cancellationToken).ConfigureAwait(false);
        return await sender(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartTorrentAsync(string torrentURL, Guid id, string folderPath, CancellationToken cancellationToken = default)
    {
        var hash = ExtractInfoHashFromMagnet(torrentURL);
        if (string.IsNullOrEmpty(hash))
        {
            throw new InvalidOperationException("Unable to extract torrent hash from magnet URL.");
        }

        var existing = await GetTorrentInfoAsync(hash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation("Torrent {Hash} already exists in qBittorrent for episode {Id}; skipping re-add", hash, id);
            return hash;
        }

        _logger.LogInformation("Starting torrent for episode {Id}", id);
        var response = await SendWithAuthRetryAsync(
            async ct =>
            {
                using var data = new MultipartFormDataContent
                {
                    { new StringContent(torrentURL), "urls" },
                    { new StringContent("false"), "paused" },
                    { new StringContent(folderPath), "savepath" },
                    { new StringContent("true"), "sequentialDownload" },
                    { new StringContent("true"), "firstLastPiecePrio" },
                    { new StringContent("false"), "create_subfolder_enabled" },
                    { new StringContent("true"), "root_folder" },
                    // Modern qBittorrent replaces create_subfolder_enabled/root_folder with this; keeps output flat in savepath.
                    { new StringContent("NoSubfolder"), "contentLayout" }
                };

                return await _client.PostAsync($"{_qbUrl}/api/v2/torrents/add", data, ct).ConfigureAwait(false);
            },
            "torrents/add",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return hash;
    }

    public async Task<TorrentInfoEntity?> GetTorrentInfoAsync(string hash, CancellationToken cancellationToken = default)
    {
        var response = await SendWithAuthRetryAsync(
            ct => _client.GetAsync(
                $"{_qbUrl}/api/v2/torrents/info?hashes={hash}",
                ct),
            "torrents/info",
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var items = JsonSerializer.Deserialize<List<TorrentInfoDto>>(json) ?? [];
        var item = items.FirstOrDefault();
        if (item is null)
        {
            return null;
        }

        var isFailed = string.Equals(item.State, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.State, "missingFiles", StringComparison.OrdinalIgnoreCase);

        var isComplete = item.Progress >= 1.0 || item.CompletionOn > 0;

        return new TorrentInfoEntity(item.Hash, item.State ?? string.Empty, item.Progress, isComplete, isFailed);
    }

    public async Task StopTorrentAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        var response = await SendWithAuthRetryAsync(
            async ct =>
            {
                using var data = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("hashes", hash)
                });

                return await _client.PostAsync($"{_qbUrl}/api/v2/torrents/pause", data, ct).ConfigureAwait(false);
            },
            "torrents/pause",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> ResolvePrimaryVideoPathAsync(string hash, string savePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(savePath))
        {
            return string.Empty;
        }

        var response = await SendWithAuthRetryAsync(
            ct => _client.GetAsync(
                $"{_qbUrl}/api/v2/torrents/files?hash={Uri.EscapeDataString(hash)}",
                ct),
            "torrents/files",
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var files = JsonSerializer.Deserialize<List<TorrentFileDto>>(json) ?? [];
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var preferred = files
            .Where(file => IsVideoFile(file.Name))
            .OrderByDescending(file => file.Size)
            .FirstOrDefault();

        if (preferred is null || string.IsNullOrWhiteSpace(preferred.Name))
        {
            return string.Empty;
        }

        var relativePath = preferred.Name.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(savePath, relativePath);
    }

    private static string ExtractInfoHashFromMagnet(string magnetUrl)
    {
        if (string.IsNullOrWhiteSpace(magnetUrl))
        {
            return string.Empty;
        }

        var marker = "xt=urn:btih:";
        var start = magnetUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = magnetUrl.IndexOf('&', start);
        var hash = end >= 0 ? magnetUrl.Substring(start, end - start) : magnetUrl[start..];
        return hash.Trim();
    }

    private sealed class TorrentInfoDto
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("completion_on")]
        public long CompletionOn { get; set; }
    }

    private sealed class TorrentFileDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    private static bool IsVideoFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _client.Dispose();
        _authLock.Dispose();
    }
}
