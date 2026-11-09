using System;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// An image sent inside an email.
/// </summary>
/// <param name="ContentId">The id the HTML refers to it by (cid:...).</param>
/// <param name="Data">The image.</param>
/// <param name="MimeType">The image's type, e.g. image/jpeg.</param>
public sealed record EmailImage(string ContentId, ReadOnlyMemory<byte> Data, string MimeType);
