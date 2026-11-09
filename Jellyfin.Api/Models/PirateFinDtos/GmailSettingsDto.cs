namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The Gmail account PirateFin sends email from, without its app password.
/// </summary>
public class GmailSettingsDto
{
    /// <summary>
    /// Gets or sets the Gmail address; empty while email isn't set up.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name emails come from.
    /// </summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether an app password is saved.
    /// </summary>
    public bool HasAppPassword { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the saved app password can't be decrypted by this server and has to be
    /// entered again.
    /// </summary>
    public bool AppPasswordUnreadable { get; set; }

    /// <summary>
    /// Gets or sets the address the buttons in emails link to; empty for no buttons.
    /// </summary>
    public string PublicAddress { get; set; } = string.Empty;
}
