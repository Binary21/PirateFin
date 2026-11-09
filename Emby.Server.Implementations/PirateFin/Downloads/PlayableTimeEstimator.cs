using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Downloads;

/// <summary>
/// Works out how far into a downloading video, in seconds, the data reaches without a gap: where a player must stop
/// before it reads pieces that aren't there yet.
/// </summary>
/// <remarks>
/// Bytes don't map to time evenly (a quiet scene takes far fewer bytes than an action scene; 48% of one episode's
/// bytes was 42% of its time, 93 s earlier than an even spread says). So ffprobe is asked where the video's keyframes
/// are, through the file's own index (Matroska cues / MP4 moov, which qBittorrent fetches first): a binary search for
/// the last keyframe before the downloaded edge, about ten lookups of ~0.2 s each, in the background. Until the first
/// search is done, or for files ffprobe can't seek in, a cautious even-spread guess is used.
/// </remarks>
public class PlayableTimeEstimator : IDisposable
{
    // The search stops when the window is this narrow (seconds); the player keeps a margin on top.
    private const double Precision = 2;

    // At most this many lookups per search.
    private const int MaxLookups = 16;

    // The even-spread guess is scaled down by this until a search has run.
    private const double FallbackShare = 0.8;

    // A video is searched again at most this often, and only once more of it is downloaded.
    private static readonly TimeSpan _searchEvery = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _lookupTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, Estimate> _estimates = new();

    // One search at a time across all videos.
    private readonly SemaphoreSlim _searching = new(1, 1);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<PlayableTimeEstimator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayableTimeEstimator"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The media encoder (where ffprobe is).</param>
    /// <param name="logger">The logger.</param>
    public PlayableTimeEstimator(IMediaEncoder mediaEncoder, ILogger<PlayableTimeEstimator> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets the latest estimate of how far a downloading video plays without a gap, starting a new search in the
    /// background when more has been downloaded since the last one.
    /// </summary>
    /// <param name="itemId">The episode or movie id.</param>
    /// <param name="path">The video file.</param>
    /// <param name="contiguousProgress">How much of the file is downloaded without a gap from its start (0-1).</param>
    /// <param name="runtimeSeconds">The video's length in seconds.</param>
    /// <returns>The position in seconds.</returns>
    public virtual double GetPlayableSeconds(Guid itemId, string path, double contiguousProgress, double runtimeSeconds)
    {
        var fallback = contiguousProgress * runtimeSeconds * FallbackShare;
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return fallback;
        }

        // qBittorrent creates the file at its full size, so its length is the size the pieces cover.
        var edgeBytes = (long)(contiguousProgress * length);
        var estimate = _estimates.GetOrAdd(itemId, _ => new Estimate());
        lock (estimate)
        {
            if (!estimate.Searching
                && !estimate.Unseekable
                && edgeBytes > estimate.EdgeBytes
                && DateTime.UtcNow - estimate.SearchedUtc >= _searchEvery)
            {
                estimate.Searching = true;
                var from = estimate.Seconds ?? 0;
                _ = Task.Run(() => SearchInBackgroundAsync(estimate, path, edgeBytes, from, runtimeSeconds));
            }

            return estimate.Unseekable || estimate.Seconds is null ? fallback : estimate.Seconds.Value;
        }
    }

    /// <summary>
    /// Forgets a video (downloaded or deleted).
    /// </summary>
    /// <param name="itemId">The episode or movie id.</param>
    public virtual void Forget(Guid itemId) => _estimates.TryRemove(itemId, out _);

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
            _searching.Dispose();
        }
    }

    /// <summary>
    /// Binary search for the last keyframe whose data starts before the downloaded edge.
    /// </summary>
    /// <param name="lookup">Finds the keyframe at or before a time: its time and byte position; <c>null</c> when ffprobe
    /// can't tell (e.g. the time lies past the downloaded edge, where the file is empty).</param>
    /// <param name="edgeBytes">Where the gap-free data ends.</param>
    /// <param name="from">A time known to be before the edge (the last estimate), seconds.</param>
    /// <param name="runtimeSeconds">The video's length.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The keyframe time in seconds; <c>null</c> when no lookup worked.</returns>
    internal static async Task<double?> SearchAsync(
        Func<double, CancellationToken, Task<(double Seconds, long Position)?>> lookup,
        long edgeBytes,
        double from,
        double runtimeSeconds,
        CancellationToken cancellationToken)
    {
        double? best = from > 0 ? from : null;
        var low = Math.Max(0, from);
        var high = runtimeSeconds;
        for (var i = 0; i < MaxLookups && high - low > Precision; i++)
        {
            var middle = (low + high) / 2;
            var found = await lookup(middle, cancellationToken).ConfigureAwait(false);
            if (found is { } keyframe && keyframe.Position < edgeBytes)
            {
                low = middle;
                best = Math.Max(best ?? 0, Math.Min(keyframe.Seconds, middle));
            }
            else
            {
                high = middle;
            }
        }

        return best;
    }

    /// <summary>
    /// Asks ffprobe for the first video packet at or after seeking to a time: through the file's index, the keyframe
    /// at or before it.
    /// </summary>
    /// <param name="path">The video file.</param>
    /// <param name="seconds">The time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Its time and byte position, or <c>null</c>.</returns>
    protected virtual async Task<(double Seconds, long Position)?> LookupAsync(string path, double seconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.ProbePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-read_intervals", seconds.ToString("0.###", CultureInfo.InvariantCulture) + "%+#1",
            "-show_entries", "packet=pts_time,pos",
            "-of", "csv=p=0",
            path
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_lookupTimeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await outputTask.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Parse(output);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }
    }

    /// <summary>
    /// Reads ffprobe's "pts_time,pos" line.
    /// </summary>
    /// <param name="output">ffprobe's output.</param>
    /// <returns>The time and position, or <c>null</c>.</returns>
    internal static (double Seconds, long Position)? Parse(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
            {
                return (time, position);
            }
        }

        return null;
    }

    private async Task SearchInBackgroundAsync(Estimate estimate, string path, long edgeBytes, double from, double runtimeSeconds)
    {
        double? seconds = null;
        await _searching.WaitAsync().ConfigureAwait(false);
        try
        {
            seconds = await SearchAsync((t, ct) => LookupAsync(path, t, ct), edgeBytes, from, runtimeSeconds, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not find the downloaded edge of {Path}", path);
        }
        finally
        {
            _searching.Release();
        }

        lock (estimate)
        {
            estimate.Searching = false;
            estimate.SearchedUtc = DateTime.UtcNow;
            estimate.EdgeBytes = edgeBytes;
            if (seconds is { } found)
            {
                estimate.Seconds = Math.Max(estimate.Seconds ?? 0, found);
            }
            else if (estimate.Seconds is null && ++estimate.Failures >= 3)
            {
                // Not one lookup worked, three times (no index, or a format ffprobe can't seek in): the cautious
                // guess it is.
                estimate.Unseekable = true;
                _logger.LogInformation("ffprobe can't seek in {Path}; the playable position is guessed from its size", path);
            }
        }
    }

    private sealed class Estimate
    {
        public double? Seconds { get; set; }

        public long EdgeBytes { get; set; }

        public DateTime SearchedUtc { get; set; } = DateTime.MinValue;

        public bool Searching { get; set; }

        public bool Unseekable { get; set; }

        public int Failures { get; set; }
    }
}
