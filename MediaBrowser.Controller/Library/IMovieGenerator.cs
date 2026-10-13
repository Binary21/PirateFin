using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Generates an episode as well as its path.
    /// </summary>
    public interface IMovieGenerator
    {
        public Task GenerateMovie(List<ScheduleMovie> scheduleMovies, string library);
    }
}
