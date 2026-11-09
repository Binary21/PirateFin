using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Emby.Server.Implementations.PirateFin.Insights;

/// <summary>
/// <see cref="IPlaybackHistory"/> in an SQLite database, &lt;data&gt;/piratefin/playback.db.
/// </summary>
public sealed class PlaybackHistoryStore : IPlaybackHistory
{
    /// <summary>
    /// How long sessions and searches are kept.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(730);

    // A search within this time that extends or shortens the previous one is the user still typing.
    private static readonly TimeSpan _typingWindow = TimeSpan.FromSeconds(60);

    private const string SessionColumns = "UserId, ItemId, SeriesId, ItemType, Library, ItemName, SeriesName, SeasonNumber, EpisodeNumber, "
        + "StartUtc, EndUtc, WatchedSeconds, PositionTicks, RuntimeTicks, Completed, Client, Device";

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (long Id, string Library, string Query, DateTime Utc)> _lastSearch = [];
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackHistoryStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackHistoryStore(IApplicationPaths appPaths, TimeProvider timeProvider, ILogger<PlaybackHistoryStore> logger)
        : this(Path.Combine(appPaths.DataPath, "piratefin", "playback.db"), timeProvider, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackHistoryStore"/> class with a database file.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="logger">The logger; <c>null</c> for none.</param>
    public PlaybackHistoryStore(string databasePath, TimeProvider timeProvider, ILogger? logger = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public long SaveSession(PlaybackSessionRecord session, long? id)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = id.HasValue
                ? "UPDATE Sessions SET EndUtc = $EndUtc, WatchedSeconds = $WatchedSeconds, PositionTicks = $PositionTicks, "
                    + "Completed = $Completed WHERE Id = $Id; SELECT $Id;"
                : $"INSERT INTO Sessions ({SessionColumns}) VALUES ($UserId, $ItemId, $SeriesId, $ItemType, $Library, $ItemName, "
                    + "$SeriesName, $SeasonNumber, $EpisodeNumber, $StartUtc, $EndUtc, $WatchedSeconds, $PositionTicks, $RuntimeTicks, "
                    + "$Completed, $Client, $Device); SELECT last_insert_rowid();";

            Bind(command, "$Id", id);
            Bind(command, "$UserId", session.UserId.ToString("N"));
            Bind(command, "$ItemId", session.ItemId.ToString("N"));
            Bind(command, "$SeriesId", session.SeriesId?.ToString("N"));
            Bind(command, "$ItemType", session.ItemType);
            Bind(command, "$Library", session.Library);
            Bind(command, "$ItemName", session.ItemName);
            Bind(command, "$SeriesName", session.SeriesName);
            Bind(command, "$SeasonNumber", session.SeasonNumber);
            Bind(command, "$EpisodeNumber", session.EpisodeNumber);
            Bind(command, "$StartUtc", ToUnix(session.StartUtc));
            Bind(command, "$EndUtc", ToUnix(session.EndUtc));
            Bind(command, "$WatchedSeconds", session.WatchedSeconds);
            Bind(command, "$PositionTicks", session.PositionTicks);
            Bind(command, "$RuntimeTicks", session.RuntimeTicks);
            Bind(command, "$Completed", session.Completed ? 1 : 0);
            Bind(command, "$Client", session.Client);
            Bind(command, "$Device", session.Device);

            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public void RecordSearch(Guid userId, string library, string query, int results)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            try
            {
                SaveSearch(userId, library, query, results, now);
            }
            catch (SqliteException ex)
            {
                // Logging a search must never break the search.
                _logger.LogError(ex, "Failed to record a search");
            }
        }
    }

    // Callers hold _lock.
    private void SaveSearch(Guid userId, string library, string query, int results, DateTime now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        var replaces = _lastSearch.TryGetValue(userId, out var last)
            && now - last.Utc <= _typingWindow
            && string.Equals(last.Library, library, StringComparison.Ordinal)
            && (query.StartsWith(last.Query, StringComparison.OrdinalIgnoreCase) || last.Query.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        command.CommandText = replaces
            ? "UPDATE Searches SET Query = $Query, Results = $Results, Utc = $Utc WHERE Id = $Id; SELECT $Id;"
            : "INSERT INTO Searches (UserId, Library, Query, Results, Utc) VALUES ($UserId, $Library, $Query, $Results, $Utc); SELECT last_insert_rowid();";

        Bind(command, "$Id", replaces ? last.Id : null);
        Bind(command, "$UserId", userId.ToString("N"));
        Bind(command, "$Library", library);
        Bind(command, "$Query", query);
        Bind(command, "$Results", results);
        Bind(command, "$Utc", ToUnix(now));

        var id = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        _lastSearch[userId] = (id, library, query, now);
    }

    /// <inheritdoc />
    public IReadOnlyList<PlaybackSessionRecord> GetSessions(Guid userId, DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {SessionColumns} FROM Sessions WHERE UserId = $UserId AND StartUtc >= $From AND StartUtc < $To ORDER BY StartUtc";
            Bind(command, "$UserId", userId.ToString("N"));
            Bind(command, "$From", ToUnix(fromUtc));
            Bind(command, "$To", ToUnix(toUtc));

            var sessions = new List<PlaybackSessionRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(new PlaybackSessionRecord
                {
                    UserId = Guid.Parse(reader.GetString(0)),
                    ItemId = Guid.Parse(reader.GetString(1)),
                    SeriesId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    ItemType = reader.GetString(3),
                    Library = GetString(reader, 4),
                    ItemName = GetString(reader, 5),
                    SeriesName = GetString(reader, 6),
                    SeasonNumber = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    EpisodeNumber = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    StartUtc = FromUnix(reader.GetInt64(9)),
                    EndUtc = FromUnix(reader.GetInt64(10)),
                    WatchedSeconds = reader.GetInt64(11),
                    PositionTicks = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                    RuntimeTicks = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                    Completed = reader.GetInt64(14) != 0,
                    Client = GetString(reader, 15),
                    Device = GetString(reader, 16)
                });
            }

            return sessions;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchRecord> GetSearches(Guid userId, DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Library, Query, Results, Utc FROM Searches WHERE UserId = $UserId AND Utc >= $From AND Utc < $To ORDER BY Utc DESC";
            Bind(command, "$UserId", userId.ToString("N"));
            Bind(command, "$From", ToUnix(fromUtc));
            Bind(command, "$To", ToUnix(toUtc));

            var searches = new List<SearchRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                searches.Add(new SearchRecord
                {
                    UserId = userId,
                    Library = reader.GetString(0),
                    Query = reader.GetString(1),
                    Results = reader.GetInt32(2),
                    Utc = FromUnix(reader.GetInt64(3))
                });
            }

            return searches;
        }
    }

    /// <inheritdoc />
    public int DeleteExpired()
    {
        var cutoff = ToUnix(_timeProvider.GetUtcNow().UtcDateTime - Retention);
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Sessions WHERE StartUtc < $Cutoff; DELETE FROM Searches WHERE Utc < $Cutoff;";
            Bind(command, "$Cutoff", cutoff);
            return command.ExecuteNonQuery();
        }
    }

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    private static string? GetString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static void Bind(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    // Callers hold _lock.
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (!_initialized)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS Sessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId TEXT NOT NULL,
                    ItemId TEXT NOT NULL,
                    SeriesId TEXT,
                    ItemType TEXT NOT NULL,
                    Library TEXT,
                    ItemName TEXT,
                    SeriesName TEXT,
                    SeasonNumber INTEGER,
                    EpisodeNumber INTEGER,
                    StartUtc INTEGER NOT NULL,
                    EndUtc INTEGER NOT NULL,
                    WatchedSeconds INTEGER NOT NULL,
                    PositionTicks INTEGER,
                    RuntimeTicks INTEGER,
                    Completed INTEGER NOT NULL,
                    Client TEXT,
                    Device TEXT);
                CREATE INDEX IF NOT EXISTS IX_Sessions_User_Start ON Sessions (UserId, StartUtc);
                CREATE TABLE IF NOT EXISTS Searches (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId TEXT NOT NULL,
                    Library TEXT NOT NULL,
                    Query TEXT NOT NULL,
                    Results INTEGER NOT NULL,
                    Utc INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Searches_User_Utc ON Searches (UserId, Utc);
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }

        return connection;
    }
}
