using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// A notification in a user's bell.
/// </summary>
public sealed class UserNotification
{
    /// <summary>
    /// Gets or sets the notification id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets what the notification is about.
    /// </summary>
    public UserNotificationKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the headline, e.g. "My Show S02E05 is ready to watch".
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the details, e.g. the admin's note.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets the item the notification is about, if any.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets when the notification was made, in UTC.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user has opened the bell since it arrived.
    /// </summary>
    public bool Seen { get; set; }
}
