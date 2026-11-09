using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.PirateFin;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// The Dub and Sub versions of anime episodes (see <see cref="EpisodeVersions"/>).
/// </summary>
public interface IEpisodeVersions
{
    /// <summary>
    /// Gets the languages the subscribers of a season want (a subscriber who didn't choose follows "Prioritize Dub").
    /// </summary>
    /// <param name="season">The season.</param>
    /// <returns>The languages; empty when nobody is subscribed.</returns>
    ReleaseLanguage[] GetWantedLanguages(Season season);

    /// <summary>
    /// Gets the language downloads default to when no user chose one ("Prioritize Dub").
    /// </summary>
    /// <returns>The language.</returns>
    ReleaseLanguage GetDefaultLanguage();

    /// <summary>
    /// Gets the episode's version in a language: the episode itself, or its hidden alternate version.
    /// </summary>
    /// <param name="episode">The episode shown in the season.</param>
    /// <param name="language">The language.</param>
    /// <returns>The version, or <c>null</c> when it has none in that language yet.</returns>
    Episode? GetVersion(Episode episode, ReleaseLanguage language);

    /// <summary>
    /// Stores a release for the episode's version in a language, creating the hidden version when needed.
    /// </summary>
    /// <param name="episode">The episode shown in the season.</param>
    /// <param name="release">The release.</param>
    /// <param name="language">The language of the release.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version that got the release.</returns>
    Task<Episode> UseReleaseAsync(Episode episode, TorrentCandidate release, ReleaseLanguage language, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the episode's hidden other-language version (and its file when downloaded and asked to), so it is
    /// searched again. A version downloading right now is kept.
    /// </summary>
    /// <param name="episode">The episode shown in the season.</param>
    /// <param name="includeDownloaded">Whether a downloaded version is removed too.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether a version was removed.</returns>
    Task<bool> RemoveAlternateAsync(Episode episode, bool includeDownloaded, CancellationToken cancellationToken);
}
