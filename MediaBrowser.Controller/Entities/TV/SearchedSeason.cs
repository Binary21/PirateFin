#nullable disable

#pragma warning disable CS1591

using System.Collections.Generic;

namespace MediaBrowser.Controller.Entities.TV
{
    public class SearchedSeason
    {
        public int? SeasonYear { get; set; } = 0;

        public string Title { get; set; } = string.Empty;

        public string AlternateTitle { get; set; } = string.Empty;

        public string RomajiTitle { get; set; } = string.Empty;

        public int Season { get; set; } = 1;

        public List<SearchedEpisode> Episodes { get; set; } = [];

        public string[] Genres { get; set; } = [];

        public string ImageUrl { get; set; } = string.Empty;

        public string Overview { get; set; } = string.Empty;
    }
}
