using System;

namespace Emby.Server.Implementations.PirateFin.YouTube;

/// <summary>
/// A YouTube Data API error.
/// </summary>
public sealed class YouTubeApiException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeApiException"/> class.
    /// </summary>
    /// <param name="reason">Google's reason code, e.g. quotaExceeded.</param>
    /// <param name="message">Google's message.</param>
    public YouTubeApiException(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Gets Google's reason code, e.g. quotaExceeded or subscriptionForbidden.
    /// </summary>
    public string Reason { get; }
}
