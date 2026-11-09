using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Reads and writes providers.json, jackett.json, qbittorrent.json and gmail.json in the server's configuration directory.
/// Saved changes are picked up without a restart.
/// </summary>
public interface IPirateFinConfigurationStore
{
    /// <summary>
    /// Gets the current Trakt / AniList / YTS settings.
    /// </summary>
    /// <returns>The settings.</returns>
    InternetProviderOptions GetProviders();

    /// <summary>
    /// Gets the current Jackett settings.
    /// </summary>
    /// <returns>The settings.</returns>
    JackettOptions GetJackett();

    /// <summary>
    /// Gets the current qBittorrent settings.
    /// </summary>
    /// <returns>The settings.</returns>
    QBitTorrentOptions GetQBitTorrent();

    /// <summary>
    /// Saves providers.json.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveProvidersAsync(InternetProviderOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Saves jackett.json.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveJackettAsync(JackettOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Saves qbittorrent.json.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveQBitTorrentAsync(QBitTorrentOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the YouTube tab's settings (the OAuth client secret is encrypted).
    /// </summary>
    /// <returns>The settings.</returns>
    YouTubeOptions GetYouTube();

    /// <summary>
    /// Saves the YouTube tab's settings to youtube.json.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveYouTubeAsync(YouTubeOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the trailer download settings.
    /// </summary>
    /// <returns>The settings.</returns>
    TrailerOptions GetTrailers();

    /// <summary>
    /// Saves the trailer download settings to trailers.json.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveTrailersAsync(TrailerOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the Gmail account emails are sent from; its app password is encrypted.
    /// </summary>
    /// <returns>The settings.</returns>
    GmailOptions GetGmail();

    /// <summary>
    /// Gets a value indicating whether the saved Gmail app password can be decrypted by this server.
    /// </summary>
    /// <returns><c>true</c> if it can; <c>false</c> if none is saved or it was encrypted elsewhere.</returns>
    bool IsGmailAppPasswordReadable();

    /// <summary>
    /// Saves the Gmail account to gmail.json, encrypting the app password.
    /// </summary>
    /// <param name="address">The Gmail address.</param>
    /// <param name="fromName">The name emails come from.</param>
    /// <param name="appPassword">The new app password; <c>null</c> keeps the saved one, empty removes it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveGmailAsync(string address, string fromName, string? appPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the address the buttons in emails link to (gmail.json).
    /// </summary>
    /// <param name="publicAddress">The address, e.g. https://piratefin.duckdns.org; empty for no buttons.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveEmailPublicAddressAsync(string publicAddress, CancellationToken cancellationToken);
}
