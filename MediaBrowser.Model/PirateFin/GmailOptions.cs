namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// The Gmail account PirateFin sends email from, loaded from the "Gmail" section of gmail.json.
/// Mail goes through smtp.gmail.com on port 587 (STARTTLS).
/// </summary>
public sealed class GmailOptions
{
    /// <summary>
    /// The name of the configuration section.
    /// </summary>
    public const string SectionName = "Gmail";

    /// <summary>
    /// Gets or sets the Gmail address, which is also the sign in name. Email is off while this is empty.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the account's app password, encrypted for the server's Windows account (DPAPI) and base64 encoded.
    /// </summary>
    public string AppPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name emails come from.
    /// </summary>
    public string FromName { get; set; } = "PirateFin";

    /// <summary>
    /// Gets or sets the address PirateFin is reached at from the internet (e.g. https://piratefin.com), used for
    /// the buttons in emails. Emails have no buttons while this is empty.
    /// </summary>
    public string PublicAddress { get; set; } = string.Empty;
}
