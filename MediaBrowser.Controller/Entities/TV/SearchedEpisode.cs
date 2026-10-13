#nullable disable

#pragma warning disable CS1591

using System;

namespace MediaBrowser.Controller.Entities.TV
{
    public class SearchedEpisode
    {
        public int Episode { get; set; } = 0;

        public DateTime? FirstAiring { get; set; }

        public string Title { get; set; }

        public string Overview { get; set; } = string.Empty;
    }
}
