using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Generates an episode as well as its path.
    /// </summary>
    public interface ISeriesGenerator
    {
        public Task GenerateSeries(SearchedSeries searchedSeries, string library);
    }
}
