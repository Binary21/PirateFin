using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// How many requests a user may still make.
/// </summary>
/// <param name="Limit">The most open requests at a time; -1 for no limit.</param>
/// <param name="Open">The user's open requests.</param>
public sealed record RequestQuota(int Limit, int Open)
{
    /// <summary>
    /// Gets the requests the user can still make; -1 when there's no limit.
    /// </summary>
    public int Remaining => Limit < 0 ? -1 : Math.Max(0, Limit - Open);
}
