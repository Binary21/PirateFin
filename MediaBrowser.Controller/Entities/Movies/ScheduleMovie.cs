#nullable disable

#pragma warning disable CS1591

using System;

namespace MediaBrowser.Controller.Entities.TV
{
    public class ScheduleMovie
    {
        public string Title { get; set; } = string.Empty;

        public string AlternateTitle { get; set; } = string.Empty;

        public string Overview { get; set; } = string.Empty;

        public DateTime? FirstAiring { get; set; }

        public string ImageUrl { get; set; } = string.Empty;

        public string MagnetUrl { get; set; } = string.Empty;

        public int? Seeders { get; set; }

        public string[] Genres { get; set; } = [];
    }
}
