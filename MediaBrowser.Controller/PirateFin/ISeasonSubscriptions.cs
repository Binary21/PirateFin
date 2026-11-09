using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.PirateFin;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Which users follow which seasons. A season downloads its new episodes while anyone is subscribed
/// (<see cref="Season.Subscribed"/>); each subscriber is told about episodes that aired after they subscribed.
/// </summary>
public interface ISeasonSubscriptions
{
    /// <summary>
    /// Gets a value indicating whether the user is subscribed to the season.
    /// </summary>
    /// <param name="seasonId">The season id.</param>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> when subscribed.</returns>
    bool IsSubscribed(Guid seasonId, Guid userId);

    /// <summary>
    /// Gets a value indicating whether the user turned the season's subscription off; watching it then doesn't
    /// subscribe them again.
    /// </summary>
    /// <param name="seasonId">The season id.</param>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> when the user unsubscribed.</returns>
    bool HasUnsubscribed(Guid seasonId, Guid userId);

    /// <summary>
    /// Gets the users subscribed to a season and since when.
    /// </summary>
    /// <param name="seasonId">The season id.</param>
    /// <returns>The subscribers.</returns>
    IReadOnlyList<SeasonSubscriber> GetSubscribers(Guid seasonId);

    /// <summary>
    /// Gets the seasons a user is subscribed to and since when.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>Season id and subscription time per season.</returns>
    IReadOnlyList<(Guid SeasonId, DateTime SinceUtc)> GetSubscriptions(Guid userId);

    /// <summary>
    /// Subscribes a user to a season (no-op when already subscribed).
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when the user wasn't subscribed yet.</returns>
    Task<bool> SubscribeAsync(Season season, Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes a user to a season in a language (anime), or changes the language of their subscription.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="language">The language; <c>null</c> keeps the one they had.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when the user wasn't subscribed yet.</returns>
    Task<bool> SubscribeAsync(Season season, Guid userId, ReleaseLanguage? language, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the language the user subscribed to a season in.
    /// </summary>
    /// <param name="seasonId">The season id.</param>
    /// <param name="userId">The user id.</param>
    /// <returns>The language, or <c>null</c> when not subscribed or subscribed before languages existed (the
    /// "Prioritize Dub" setting decides those).</returns>
    ReleaseLanguage? GetLanguage(Guid seasonId, Guid userId);

    /// <summary>
    /// Unsubscribes a user from a season; the season stops downloading new episodes when nobody is left.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task UnsubscribeAsync(Season season, Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// A user subscribed to a season.
/// </summary>
/// <param name="UserId">The user id.</param>
/// <param name="SinceUtc">When they subscribed.</param>
/// <param name="Language">The language they subscribed in (anime), <c>null</c> when they didn't choose one.</param>
public sealed record SeasonSubscriber(Guid UserId, DateTime SinceUtc, ReleaseLanguage? Language = null);
