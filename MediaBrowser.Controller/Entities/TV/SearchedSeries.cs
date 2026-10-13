#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;

namespace MediaBrowser.Controller.Entities.TV
{
    public class SearchedSeries
    {
        public List<SearchedSeason> Seasons { get; set; } = [];

        public string Title { get; set; } = string.Empty;

        public string AlternateTitle { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        public string[] Genres { get; set; } = [];

        public string AniListId { get; set; } = string.Empty;
    }
}
