using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// The release search pauses, saved in &lt;data&gt;/piratefin/release-search-pauses.json.
/// </summary>
public sealed class ReleaseSearchPauseStore : IReleaseSearchPauses
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<ReleaseSearchPauseStore> _logger;
    private readonly Lock _lock = new();
    private PauseState? _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReleaseSearchPauseStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ReleaseSearchPauseStore(IApplicationPaths appPaths, ILogger<ReleaseSearchPauseStore> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public DateTime? AllPausedUtc
    {
        get
        {
            lock (_lock)
            {
                return Load().AllPausedUtc;
            }
        }
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "release-search-pauses.json");

    /// <inheritdoc />
    public DateTime? GetShowPausedUtc(Guid seriesId)
    {
        lock (_lock)
        {
            return Load().Shows.TryGetValue(seriesId, out var at) ? at : null;
        }
    }

    /// <inheritdoc />
    public bool IsPaused(Guid seriesId)
    {
        lock (_lock)
        {
            var state = Load();
            return state.AllPausedUtc.HasValue || state.Shows.ContainsKey(seriesId);
        }
    }

    /// <inheritdoc />
    public void PauseAll(DateTime utcNow)
    {
        lock (_lock)
        {
            var state = Load();
            if (!state.AllPausedUtc.HasValue)
            {
                state.AllPausedUtc = utcNow;
                Save();
            }
        }
    }

    /// <inheritdoc />
    public DateTime? ResumeAll()
    {
        lock (_lock)
        {
            var state = Load();
            var at = state.AllPausedUtc;
            if (at.HasValue)
            {
                state.AllPausedUtc = null;
                Save();
            }

            return at;
        }
    }

    /// <inheritdoc />
    public void PauseShow(Guid seriesId, DateTime utcNow)
    {
        lock (_lock)
        {
            if (Load().Shows.TryAdd(seriesId, utcNow))
            {
                Save();
            }
        }
    }

    /// <inheritdoc />
    public DateTime? ResumeShow(Guid seriesId)
    {
        lock (_lock)
        {
            if (!Load().Shows.Remove(seriesId, out var at))
            {
                return null;
            }

            Save();
            return at;
        }
    }

    private PauseState Load()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            _state = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<PauseState>(File.ReadAllText(FilePath)) ?? new PauseState()
                : new PauseState();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; starting without release search pauses", FilePath);
            _state = new PauseState();
        }

        return _state;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
        File.Move(temp, FilePath, true);
    }

    private sealed class PauseState
    {
        public DateTime? AllPausedUtc { get; set; }

        public Dictionary<Guid, DateTime> Shows { get; set; } = [];
    }
}
