using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Generates an episode as well as its path.
    /// </summary>
    public interface IEpisodeGenerator
    {
        public Task GenerateEpisode(List<ScheduleEpisode> scheduleEpisodes, string library, System.DateTime startOfWeek);
    }
}
