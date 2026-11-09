using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Torrents;

/// <summary>
/// qBittorrent Web API client. Starts qBittorrent on demand when <see cref="QBitTorrentOptions.ExecutablePath"/> is set.
/// </summary>
public class QBitTorrentClient : IDisposable
{
    private static readonly string[] _videoExtensions = [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".wmv", ".webm"];

    private readonly HttpClient _client;
    private readonly IOptionsMonitor<QBitTorrentOptions> _options;
    private readonly ILogger<QBitTorrentClient> _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    // The url / credentials the current session cookie belongs to; settings changed in the dashboard force a new login.
    private string? _authenticatedAs;

    /// <summary>
    /// Initializes a new instance of the <see cref="QBitTorrentClient"/> class.
    /// </summary>
    /// <param name="options">The qBittorrent options.</param>
    /// <param name="logger">The logger.</param>
    public QBitTorrentClient(IOptionsMonitor<QBitTorrentOptions> options, ILogger<QBitTorrentClient> logger)
        : this(options, logger, new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() })
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="QBitTorrentClient"/> class with a custom handler (tests).
    /// </summary>
    /// <param name="options">The qBittorrent options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="handler">The http handler.</param>
    internal QBitTorrentClient(IOptionsMonitor<QBitTorrentOptions> options, ILogger<QBitTorrentClient> logger, HttpMessageHandler handler)
    {
        _options = options;
        _logger = logger;
        _client = new HttpClient(handler);
    }

    private string BaseUrl => _options.CurrentValue.Url.TrimEnd('/');

    /// <summary>
    /// Adds a torrent unless qBittorrent already has it.
    /// </summary>
    /// <param name="magnetUrl">The magnet (or .torrent) url.</param>
    /// <param name="savePath">The folder to download into.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The info hash.</returns>
    public virtual async Task<string> AddAsync(string magnetUrl, string savePath, CancellationToken cancellationToken)
    {
        var hash = GetHexInfoHash(magnetUrl);
        if (hash.Length == 0)
        {
            throw new InvalidOperationException("Unable to read the info hash from the magnet url.");
        }

        if (await GetInfoAsync(hash, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (!existing.IsFailed)
            {
                return hash;
            }

            // An old entry whose files are gone (e.g. the episode was deleted and is downloaded again) would never
            // start; it's replaced by a fresh one.
            _logger.LogInformation("Replacing the failed ({State}) torrent {Hash} in qBittorrent", existing.State, hash);
            await DeleteAsync(hash, false, cancellationToken).ConfigureAwait(false);
            await WaitUntilRemovedAsync(hash, cancellationToken).ConfigureAwait(false);
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v2/torrents/add")
            {
                Content = new MultipartFormDataContent
                {
                    { new StringContent(magnetUrl), "urls" },
                    { new StringContent(savePath), "savepath" },
                    { new StringContent("false"), "paused" },
                    { new StringContent("false"), "stopped" },
                    { new StringContent("true"), "sequentialDownload" },
                    { new StringContent("true"), "firstLastPiecePrio" },

                    // Keep the files flat in the save path (qBittorrent 4.3.2+; older versions use root_folder).
                    { new StringContent("NoSubfolder"), "contentLayout" },
                    { new StringContent("false"), "root_folder" }
                }
            },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return hash;
    }

    /// <summary>
    /// Gets a torrent's state.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state, or <c>null</c> when qBittorrent doesn't have the torrent.</returns>
    public virtual async Task<TorrentInfo?> GetInfoAsync(string hash, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v2/torrents/info?hashes=" + Uri.EscapeDataString(hash)),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var items = await ReadJsonAsync<List<TorrentInfoDto>>(response, cancellationToken).ConfigureAwait(false);
        var item = items?.FirstOrDefault();
        if (item is null)
        {
            return null;
        }

        return ToInfo(item);
    }

    /// <summary>
    /// Gets several torrents' states in one request.
    /// </summary>
    /// <param name="hashes">The info hashes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The torrents qBittorrent has, by hash (case-insensitive); missing ones were removed from it.</returns>
    public virtual async Task<IReadOnlyDictionary<string, TorrentInfo>> GetInfosAsync(IReadOnlyCollection<string> hashes, CancellationToken cancellationToken)
    {
        if (hashes.Count == 0)
        {
            return new Dictionary<string, TorrentInfo>();
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v2/torrents/info?hashes=" + Uri.EscapeDataString(string.Join('|', hashes))),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var items = await ReadJsonAsync<List<TorrentInfoDto>>(response, cancellationToken).ConfigureAwait(false);
        var infos = new Dictionary<string, TorrentInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items ?? [])
        {
            infos[item.Hash] = ToInfo(item);
        }

        return infos;
    }

    /// <summary>
    /// Gets every torrent in qBittorrent.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The torrents.</returns>
    public virtual async Task<IReadOnlyList<TorrentInfo>> GetAllAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v2/torrents/info"),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var items = await ReadJsonAsync<List<TorrentInfoDto>>(response, cancellationToken).ConfigureAwait(false);
        return items?.Select(ToInfo).ToList() ?? [];
    }

    /// <summary>
    /// Removes a torrent from qBittorrent.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <param name="deleteFiles">Whether its downloaded files are deleted too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual async Task DeleteAsync(string hash, bool deleteFiles, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v2/torrents/delete")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["hashes"] = hash,
                    ["deleteFiles"] = deleteFiles ? "true" : "false"
                })
            },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static TorrentInfo ToInfo(TorrentInfoDto item)
    {
        var isFailed = item.State is "error" or "missingFiles";
        // completion_on can't be trusted: older versions report 4294967295 for unfinished torrents.
        return new TorrentInfo(item.Hash, item.State ?? string.Empty, item.Progress, item.Progress >= 1.0, isFailed, item.Eta, item.ContentPath);
    }

    // qBittorrent removes torrents in the background; adding the same one again before that is ignored.
    private async Task WaitUntilRemovedAsync(string hash, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await GetInfoAsync(hash, cancellationToken).ConfigureAwait(false) is null)
            {
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the files of a torrent; empty until the magnet's metadata has been downloaded.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The files.</returns>
    public virtual async Task<IReadOnlyList<TorrentFile>> GetFilesAsync(string hash, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v2/torrents/files?hash=" + Uri.EscapeDataString(hash)),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var files = await ReadJsonAsync<List<TorrentFileDto>>(response, cancellationToken).ConfigureAwait(false);
        return files?.Select((f, i) => new TorrentFile(
                f.Index ?? i,
                f.Name,
                f.Size,
                f.PieceRange is [var first, var last] ? first : null,
                f.PieceRange is [_, var end] ? end : null))
            .ToList() ?? [];
    }

    /// <summary>
    /// Gets the state of each piece of a torrent: 0 not downloaded, 1 downloading, 2 downloaded.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The states, in piece order; empty until the magnet's metadata has been downloaded.</returns>
    public virtual async Task<IReadOnlyList<int>> GetPieceStatesAsync(string hash, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/api/v2/torrents/pieceStates?hash=" + Uri.EscapeDataString(hash)),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await ReadJsonAsync<List<int>>(response, cancellationToken).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Gets how much of a file is downloaded without a gap from its start: what a player can read before it reaches
    /// data that isn't there yet (qBittorrent's "sequential" download still gets pieces out of order).
    /// </summary>
    /// <param name="pieceStates">The torrent's piece states (see <see cref="GetPieceStatesAsync"/>).</param>
    /// <param name="firstPiece">The file's first piece.</param>
    /// <param name="lastPiece">The file's last piece.</param>
    /// <returns>The share of the file's pieces, between 0 and 1; <c>null</c> when the states don't cover the file.</returns>
    public static double? GetContiguousProgress(IReadOnlyList<int> pieceStates, int firstPiece, int lastPiece)
    {
        if (firstPiece < 0 || lastPiece < firstPiece || lastPiece >= pieceStates.Count)
        {
            return null;
        }

        var done = 0;
        while (firstPiece + done <= lastPiece && pieceStates[firstPiece + done] == 2)
        {
            done++;
        }

        return (double)done / (lastPiece - firstPiece + 1);
    }

    /// <summary>
    /// Sets the download priority of files in a torrent; priority 0 skips them.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <param name="fileIndexes">The file indexes.</param>
    /// <param name="priority">The priority (0 = do not download, 1 = normal).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual async Task SetFilePriorityAsync(string hash, IEnumerable<int> fileIndexes, int priority, CancellationToken cancellationToken)
    {
        var ids = string.Join('|', fileIndexes);
        if (ids.Length == 0)
        {
            return;
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v2/torrents/filePrio")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["hash"] = hash,
                    ["id"] = ids,
                    ["priority"] = priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
                })
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("qBittorrent refused changing file priorities of {Hash}: {StatusCode}", hash, response.StatusCode);
        }
    }

    /// <summary>
    /// Renames a file inside a torrent. qBittorrent handles this safely while downloading.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <param name="oldPath">The current relative path.</param>
    /// <param name="newPath">The new relative path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether qBittorrent accepted the rename.</returns>
    public virtual async Task<bool> RenameFileAsync(string hash, string oldPath, string newPath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/v2/torrents/renameFile")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["hash"] = hash,
                    ["oldPath"] = oldPath,
                    ["newPath"] = newPath
                })
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("qBittorrent refused renaming {OldPath} to {NewPath}: {StatusCode}", oldPath, newPath, response.StatusCode);
        }

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Stops (pauses) a torrent.
    /// </summary>
    /// <param name="hash">The info hash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public virtual async Task StopAsync(string hash, CancellationToken cancellationToken)
    {
        // qBittorrent 5 renamed "pause" to "stop".
        foreach (var endpoint in new[] { "/api/v2/torrents/stop", "/api/v2/torrents/pause" })
        {
            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = hash })
                },
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                return;
            }
        }
    }

    /// <summary>
    /// Picks the main video file of a torrent (the largest one).
    /// </summary>
    /// <param name="files">The torrent's files.</param>
    /// <returns>The file, or <c>null</c>.</returns>
    public static TorrentFile? GetPrimaryVideo(IReadOnlyList<TorrentFile> files)
        => files
            .Where(f => _videoExtensions.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase))
            .MaxBy(f => f.Size);

    /// <summary>
    /// Gets the hex info hash qBittorrent uses, converting base32 hashes.
    /// </summary>
    /// <param name="magnetUrl">The magnet url.</param>
    /// <returns>The lower-case hex hash, or an empty string.</returns>
    public static string GetHexInfoHash(string magnetUrl)
    {
        var hash = ReleaseTitleParser.GetInfoHash(magnetUrl);
        if (hash.Length == 40)
        {
            return hash.ToLowerInvariant();
        }

        return hash.Length == 32 ? Convert.ToHexString(FromBase32(hash)).ToLowerInvariant() : string.Empty;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources.
    /// </summary>
    /// <param name="disposing">Whether called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
            _authLock.Dispose();
        }
    }

    private static byte[] FromBase32(string value)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[value.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in value)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }

        return bytes;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    // Sends with a login first, and re-logs in once when the session cookie has expired.
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
        using (var request = createRequest())
        {
            var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                return response;
            }

            response.Dispose();
        }

        _authenticatedAs = null;
        await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
        using var retry = createRequest();
        return await _client.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static string GetIdentity(QBitTorrentOptions options) => options.Url + "\n" + options.Username + "\n" + options.Password;

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var identity = GetIdentity(options);
        if (_authenticatedAs == identity)
        {
            return;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_authenticatedAs == identity)
            {
                return;
            }

            await EnsureRunningAsync(options, cancellationToken).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = options.Username,
                ["password"] = options.Password
            });
            using var response = await _client.PostAsync(options.Url.TrimEnd('/') + "/api/v2/auth/login", content, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || text.Contains("Fail", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"qBittorrent login failed: {text}");
            }

            _authenticatedAs = identity;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task EnsureRunningAsync(QBitTorrentOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath) || Process.GetProcessesByName("qbittorrent").Length > 0)
        {
            return;
        }

        if (!File.Exists(options.ExecutablePath))
        {
            _logger.LogWarning("qBittorrent is not running and was not found at {Path}", options.ExecutablePath);
            return;
        }

        _logger.LogInformation("Starting qBittorrent");
        Process.Start(new ProcessStartInfo { FileName = options.ExecutablePath, UseShellExecute = true })?.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
    }

    private sealed class TorrentInfoDto
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("eta")]
        public long? Eta { get; set; }

        [JsonPropertyName("content_path")]
        public string? ContentPath { get; set; }
    }

    private sealed class TorrentFileDto
    {
        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("piece_range")]
        public int[]? PieceRange { get; set; }
    }
}
