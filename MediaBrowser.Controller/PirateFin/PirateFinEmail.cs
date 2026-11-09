using System.Collections.Generic;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// An email: HTML with a plain text alternative, and the images the HTML shows (referenced as cid:{ContentId}).
/// </summary>
/// <param name="Subject">The subject.</param>
/// <param name="Html">The HTML body.</param>
/// <param name="Text">The plain text body, for mail apps that don't show HTML.</param>
/// <param name="Images">The images sent inside the email.</param>
public sealed record PirateFinEmail(string Subject, string Html, string Text, IReadOnlyList<EmailImage> Images);
