using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Trailers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.YouTube;

/// <summary>
/// Streams YouTube videos without storing them: yt-dlp finds the video's HLS streams (up to 1080p, H.264 + AAC where
/// available) and ffmpeg copies them, without converting, into an HLS playlist in a temporary folder that Jellyfin's
/// player plays. A downloaded video is streamed from its file the same way. Each user has one stream at a time; a
/// stream nobody has asked for in <see cref="IdleTimeout"/> is stopped and its folder deleted.
/// </summary>
public sealed partial class YouTubeStreams : IDisposable
{
    /// <summary>
    /// How long a stream lives without requests.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    // HLS first: YouTube serves it in small pieces, which ffmpeg reads at full speed (one long download gets throttled).
    private const string StreamFormat = "bv*[height<=1080][vcodec^=avc1][protocol=m3u8_native]+ba[protocol=m3u8_native]"
        + "/b[height<=1080][protocol=m3u8_native]"
        + "/bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/b[height<=1080]";

    private static readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan _fileWait = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly YtDlp _ytDlp;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<YouTubeStreams> _logger;
    private readonly Timer _cleanup;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeStreams"/> class.
    /// </summary>
    /// <param name="ytDlp">The yt-dlp tool.</param>
    /// <param name="mediaEncoder">The media encoder, for ffmpeg.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public YouTubeStreams(YtDlp ytDlp, IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger<YouTubeStreams> logger)
    {
        _ytDlp = ytDlp;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
        _cleanup = new Timer(_ => StopIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        // Leftovers of streams from before a restart.
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Couldn't clear {Path}", Root);
        }
    }

    private string Root => Path.Combine(_appPaths.CachePath, "piratefin-youtube");

    /// <summary>
    /// Gets a value indicating whether a stream file name is one this serves (the playlist or a segment).
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <returns><c>true</c> if it is.</returns>
    public static bool IsStreamFile(string? fileName) => fileName is not null && StreamFileRegex().IsMatch(fileName);

    /// <summary>
    /// Starts a stream and waits until it can play.
    /// </summary>
    /// <param name="userId">The user id (their earlier stream is stopped).</param>
    /// <param name="videoId">The video id.</param>
    /// <param name="localFile">The video's download, when there is one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream's token.</returns>
    /// <exception cref="InvalidOperationException">The video can't be streamed (the reason).</exception>
    public async Task<string> StartAsync(Guid userId, string videoId, string? localFile, CancellationToken cancellationToken)
    {
        foreach (var old in _sessions.Values.Where(s => s.UserId.Equals(userId)).ToList())
        {
            Stop(old);
        }

        var inputs = localFile is not null ? [localFile] : await ResolveAsync(videoId, cancellationToken).ConfigureAwait(false);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var folder = Path.Combine(Root, token);
        Directory.CreateDirectory(folder);

        var args = new List<string> { "-v", "error", "-y" };
        foreach (var input in inputs)
        {
            args.AddRange(["-i", input]);
        }

        args.AddRange(["-map", "0:v:0", "-map", inputs.Count > 1 ? "1:a:0" : "0:a:0?", "-c", "copy",
            "-f", "hls", "-hls_time", "6", "-hls_list_size", "0", "-hls_playlist_type", "event",
            "-hls_segment_filename", Path.Combine(folder, "seg%05d.ts"), Path.Combine(folder, "index.m3u8")]);

        var ffmpeg = string.IsNullOrEmpty(_mediaEncoder.EncoderPath) ? "ffmpeg" : _mediaEncoder.EncoderPath;
        var startInfo = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && errors.Length < 4000)
            {
                errors.AppendLine(e.Data);
            }
        };
        process.Start();
        process.BeginErrorReadLine();

        var session = new Session(token, userId, videoId, folder, process) { LastAccessUtc = DateTime.UtcNow };
        _sessions[token] = session;

        // Ready once a couple of segments are written (or the whole video, if it's that short).
        var deadline = DateTime.UtcNow + _startTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CountSegments(folder) >= 2 || (process.HasExited && CountSegments(folder) >= 1))
            {
                return token;
            }

            if (process.HasExited)
            {
                break;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("The YouTube stream of {VideoId} didn't start: {Errors}", videoId, errors.ToString().Trim());
        Stop(session);
        throw new InvalidOperationException("The video couldn't be started. Please try again.");
    }

    /// <summary>
    /// Gets a file of a stream, waiting a little for a segment being written.
    /// </summary>
    /// <param name="token">The stream's token.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path, or <c>null</c>.</returns>
    public async Task<string?> GetFileAsync(string token, string fileName, CancellationToken cancellationToken)
    {
        if (!IsStreamFile(fileName) || !_sessions.TryGetValue(token, out var session))
        {
            return null;
        }

        session.LastAccessUtc = DateTime.UtcNow;
        var path = Path.Combine(session.Folder, fileName);
        var deadline = DateTime.UtcNow + _fileWait;
        while (!File.Exists(path) && !session.Process.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return File.Exists(path) ? path : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cleanup.Dispose();
        foreach (var session in _sessions.Values.ToList())
        {
            Stop(session);
        }
    }

    private static int CountSegments(string folder)
        => Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "seg*.ts").Count() : 0;

    private async Task<List<string>> ResolveAsync(string videoId, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await _ytDlp.RunAsync(
            ["--no-playlist", "--no-warnings", "-f", StreamFormat, "-g", "https://www.youtube.com/watch?v=" + videoId],
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);

        var urls = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (exitCode == 0 && urls.Count > 0)
        {
            return urls;
        }

        _logger.LogWarning("yt-dlp couldn't find a stream for {VideoId}: {Output}", videoId, output.Trim());
        if (output.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This video is age restricted and can't be played here.");
        }

        if (output.Contains("not a bot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("YouTube is temporarily refusing this server. Please try again later.");
        }

        throw new InvalidOperationException("This video can't be played (it may be private, removed or members-only).");
    }

    private void StopIdle()
    {
        foreach (var session in _sessions.Values.Where(s => DateTime.UtcNow - s.LastAccessUtc > IdleTimeout).ToList())
        {
            Stop(session);
        }
    }

    private void Stop(Session session)
    {
        _sessions.TryRemove(session.Token, out _);
        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        session.Process.Dispose();

        // ffmpeg may need a moment to let go of its files.
        _ = Task.Delay(1000).ContinueWith(
            _ =>
            {
                try
                {
                    if (Directory.Exists(session.Folder))
                    {
                        Directory.Delete(session.Folder, true);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Couldn't delete {Path}", session.Folder);
                }
            },
            TaskScheduler.Default);
    }

    [GeneratedRegex(@"^(index\.m3u8|seg\d{5}\.ts)$")]
    private static partial Regex StreamFileRegex();

    private sealed class Session(string token, Guid userId, string videoId, string folder, Process process)
    {
        public string Token { get; } = token;

        public Guid UserId { get; } = userId;

        public string VideoId { get; } = videoId;

        public string Folder { get; } = folder;

        public Process Process { get; } = process;

        public DateTime LastAccessUtc { get; set; }
    }
}
