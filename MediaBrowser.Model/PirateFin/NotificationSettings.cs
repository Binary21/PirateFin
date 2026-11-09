namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A user's email address and whether they want PirateFin's emails.
/// </summary>
public class NotificationSettings
{
    /// <summary>
    /// Gets or sets the email address, or <c>null</c>.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user gets an email when a new episode of a season they follow is
    /// ready. Request updates are emailed regardless, as long as there is an address.
    /// </summary>
    public bool NewEpisodeEmails { get; set; } = true;
}
