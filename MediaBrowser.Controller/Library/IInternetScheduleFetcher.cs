using System.Collections.Generic;
using System.Threading.Tasks;

using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Manages the episodes being aired this week.
    /// </summary>
    public interface IInternetScheduleFetcher
    {
        /// <summary>
        /// Gets the Anime Episodes being aired this week.
        /// </summary>
        /// <returns> Returns a ScheduleEpisode object.</returns>
        Task<List<ScheduleEpisode>> GetAnimeWeeklySchedule();

        /// <summary>
        /// Gets the TV Show Episodes being aired this week.
        /// </summary>
        /// <returns> Returns a ScheduleEpisode object.</returns>
        Task<List<ScheduleEpisode>> GetTVShowWeeklySchedule();

        /// <summary>
        /// Gets the Movies being aired this year.
        /// </summary>
        /// <returns> Returns a ScheduleEpisode object.</returns>
        Task<List<ScheduleMovie>> GetMovieLatestSchedule();
    }
}
