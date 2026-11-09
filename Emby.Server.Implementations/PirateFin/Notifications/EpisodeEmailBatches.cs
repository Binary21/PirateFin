using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// Remembers, per user, which downloaded episodes wait for an earlier episode of their series before they are emailed
/// (held), and which were already listed in an email before they finished (announced), so episodes that download
/// together get one email. Saved in &lt;data&gt;/piratefin/episode-emails.json so a restart doesn't lose them.
/// </summary>
public sealed class EpisodeEmailBatches
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<EpisodeEmailBatches> _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, UserBatches>? _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeEmailBatches"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public EpisodeEmailBatches(IApplicationPaths appPaths, ILogger<EpisodeEmailBatches> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_appPaths.DataPath, "piratefin", "episode-emails.json");

    /// <summary>
    /// Forgets an episode that was already listed in an email.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="episodeId">The episode id.</param>
    /// <returns><c>true</c> when it was listed, so it gets no email of its own.</returns>
    public bool TryTakeAnnounced(Guid userId, Guid episodeId)
    {
        lock (_lock)
        {
            if (!Get(userId).Announced.Remove(episodeId))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>
    /// Remembers episodes listed in an email before they finished downloading.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="episodeIds">The episode ids.</param>
    public void Announce(Guid userId, IEnumerable<Guid> episodeIds)
    {
        lock (_lock)
        {
            var announced = Get(userId).Announced;
            announced.AddRange(episodeIds.Where(id => !announced.Contains(id)));
            Save();
        }
    }

    /// <summary>
    /// Holds a downloaded episode back until an earlier episode of its series finishes.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="episodeId">The episode id.</param>
    public void Hold(Guid userId, Guid episodeId)
    {
        lock (_lock)
        {
            var held = Get(userId).Held;
            if (!held.Contains(episodeId))
            {
                held.Add(episodeId);
                Save();
            }
        }
    }

    /// <summary>
    /// Takes the held episodes that belong in an email.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="belongs">Whether a held episode belongs (e.g. is of the same series).</param>
    /// <returns>Their ids.</returns>
    public IReadOnlyList<Guid> TakeHeld(Guid userId, Func<Guid, bool> belongs)
    {
        lock (_lock)
        {
            var held = Get(userId).Held;
            var taken = held.Where(belongs).ToList();
            if (taken.Count > 0)
            {
                held.RemoveAll(taken.Contains);
                Save();
            }

            return taken;
        }
    }

    private static string Key(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private UserBatches Get(Guid userId)
    {
        var data = Load();
        if (!data.TryGetValue(Key(userId), out var batches))
        {
            batches = new UserBatches();
            data[Key(userId)] = batches;
        }

        return batches;
    }

    private Dictionary<string, UserBatches> Load()
    {
        if (_data is not null)
        {
            return _data;
        }

        try
        {
            _data = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, UserBatches>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{Path} is unreadable; starting without held episode emails", FilePath);
            _data = [];
        }

        return _data;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Load(), _jsonOptions));
        File.Move(temp, FilePath, true);
    }

    private sealed class UserBatches
    {
        public List<Guid> Held { get; set; } = [];

        public List<Guid> Announced { get; set; } = [];
    }
}
