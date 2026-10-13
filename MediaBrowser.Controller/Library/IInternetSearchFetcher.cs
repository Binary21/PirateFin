using System.Collections.Generic;
using System.Threading.Tasks;

using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Manages the episodes being aired this week.
    /// </summary>
    public interface IInternetSearchFetcher
    {
        /// <summary>
        /// Gets the Anime Episodes being aired this week.
        /// </summary>
        /// <param name="searchQuery"> the search query passed in by the user. </param>
        /// <returns> Returns a ScheduleEpisode object.</returns>
        public Task<SearchedSeries> GetAnimeSeries(string searchQuery);

        /// <summary>
        /// Gets the TV Show Episodes being aired this week.
        /// </summary>
        /// <param name="searchQuery"> the search query passed in by the user. </param>
        /// <returns> Returns a ScheduleEpisode object.</returns>
        Task<List<SearchedSeries>> GetTVShowSeries(string searchQuery);
    }
}
