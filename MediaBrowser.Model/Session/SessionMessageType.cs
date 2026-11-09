#pragma warning disable CS1591

namespace MediaBrowser.Model.Session
{
    /// <summary>
    /// The different kinds of messages that are used in the WebSocket api.
    /// </summary>
    public enum SessionMessageType
    {
        // Server -> Client
        ForceKeepAlive,
        GeneralCommand,
        UserDataChanged,
        Sessions,
        Play,
        SyncPlayCommand,
        SyncPlayGroupUpdate,
        Playstate,
        RestartRequired,
        ServerShuttingDown,
        ServerRestarting,
        LibraryChanged,
        UserDeleted,
        UserUpdated,
        SeriesTimerCreated,
        TimerCreated,
        SeriesTimerCancelled,
        TimerCancelled,
        RefreshProgress,
        ScheduledTaskEnded,
        PackageInstallationCancelled,
        PackageInstallationFailed,
        PackageInstallationCompleted,
        PackageInstalling,
        PackageUninstalled,
        ActivityLogEntry,
        ScheduledTasksInfo,

        // PirateFin: a watched item's download status (to the connections that asked for it).
        PirateFinDownloadProgress,

        // Client -> Server
        ActivityLogEntryStart,
        ActivityLogEntryStop,
        SessionsStart,
        SessionsStop,
        ScheduledTasksInfoStart,
        ScheduledTasksInfoStop,

        // PirateFin: start / stop following an item's download (data: the item id). Start follows it until it's
        // playable (a Play button); Follow until it's fully downloaded (the player, watching the downloaded edge).
        PirateFinDownloadStart,
        PirateFinDownloadStop,
        PirateFinDownloadFollow,

        // Shared
        KeepAlive,
    }
}
