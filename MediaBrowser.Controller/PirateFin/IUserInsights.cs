using System;
using System.Collections.Generic;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Builds the dashboard's view of what each user watches.
/// </summary>
public interface IUserInsights
{
    /// <summary>
    /// Gets everything the Insights tab shows for a user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="utcOffset">The viewer's offset from UTC, for days and hours.</param>
    /// <returns>The insights, or <c>null</c> when the user doesn't exist.</returns>
    UserInsights? GetInsights(Guid userId, TimeSpan utcOffset);

    /// <summary>
    /// Gets the summary line of every user, for the Users list.
    /// </summary>
    /// <returns>One summary per user.</returns>
    IReadOnlyList<UserInsightSummary> GetSummaries();
}
