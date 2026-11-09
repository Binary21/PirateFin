namespace Emby.Server.Implementations.PirateFin.Torrents;

/// <summary>
/// What a release title says about its content.
/// </summary>
/// <param name="Season">The season, if stated.</param>
/// <param name="Episode">The episode, if stated.</param>
/// <param name="IsPack">Whether the release is a batch / season pack rather than a single episode.</param>
public readonly record struct ReleaseInfo(int? Season, int? Episode, bool IsPack);
