using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Trailers;

/// <summary>
/// The yt-dlp tool, kept in &lt;data&gt;/piratefin/tools: downloaded on first use from its official GitHub releases
/// and updated weekly with its own updater (YouTube changes often and old versions stop working).
/// </summary>
public sealed class YtDlp : IDisposable
{
    private static readonly TimeSpan _updateEvery = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<YtDlp> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlp"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public YtDlp(IHttpClientFactory httpClientFactory, IApplicationPaths appPaths, ILogger<YtDlp> logger)
    {
        _httpClientFactory = httpClientFactory;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private string ToolPath => Path.Combine(_appPaths.DataPath, "piratefin", "tools", OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>
    /// Runs yt-dlp, first downloading or updating it when needed.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="timeout">How long it may run.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exit code and what it printed.</returns>
    public async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tool = await EnsureAsync(cancellationToken).ConfigureAwait(false);
        var args = new List<string>(arguments);

        // YouTube needs a JavaScript runtime for some formats; use Deno or Node.js when installed.
        var runtime = FindRuntime();
        if (runtime is not null)
        {
            args.InsertRange(0, ["--js-runtimes", runtime]);
        }

        return await RunProcessAsync(tool, args, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a program and collects everything it prints.
    /// </summary>
    /// <param name="fileName">The program.</param>
    /// <param name="arguments">The arguments, passed as they are (no shell).</param>
    /// <param name="timeout">How long it may run before it's killed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exit code (-1 when killed) and the output.</returns>
    internal static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            cancellationToken.ThrowIfCancellationRequested();
            return (-1, Read(output) + Environment.NewLine + "Timed out.");
        }

        return (process.ExitCode, Read(output));
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            // Progress lines can be long; only the end matters.
            if (output.Length > 64_000)
            {
                output.Remove(0, output.Length - 32_000);
            }

            output.AppendLine(line);
        }
    }

    private static string Read(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private static string? FindRuntime()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        foreach (var name in new[] { "deno", "node" })
        {
            var found = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir.Trim('"'), name + extension))
                .FirstOrDefault(File.Exists);
            if (found is not null)
            {
                return $"{name}:{found}";
            }
        }

        return null;
    }

    private async Task<string> EnsureAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tool = ToolPath;
            if (!File.Exists(tool))
            {
                await DownloadAsync(tool, cancellationToken).ConfigureAwait(false);
            }
            else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tool) > _updateEvery)
            {
                var (exitCode, output) = await RunProcessAsync(tool, ["-U"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    _logger.LogWarning("yt-dlp couldn't update itself: {Output}", output.Trim());
                }

                // Check again in a week either way.
                File.SetLastWriteTimeUtc(tool, DateTime.UtcNow);
            }

            return tool;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DownloadAsync(string tool, CancellationToken cancellationToken)
    {
        var asset = OperatingSystem.IsWindows() ? "yt-dlp.exe" : OperatingSystem.IsMacOS() ? "yt-dlp_macos" : "yt-dlp_linux";
        var url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}";
        _logger.LogInformation("Downloading yt-dlp from {Url} for the billboard trailers", url);

        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        var temp = tool + ".download";
        using (var response = await _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (file.ConfigureAwait(false))
            {
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.Move(temp, tool, true);
    }
}
