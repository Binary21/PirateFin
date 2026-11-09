namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The Gmail account to send email from.
/// </summary>
public class SaveGmailSettingsDto
{
    /// <summary>
    /// Gets or sets the Gmail address; empty turns email off.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name emails come from.
    /// </summary>
    public string FromName { get; set; } = "PirateFin";

    /// <summary>
    /// Gets or sets the 16 letter app password (spaces are ignored); <c>null</c> or empty keeps the saved one.
    /// </summary>
    public string? AppPassword { get; set; }
}
