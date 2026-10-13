#nullable disable

#pragma warning disable CS1591

using System;

namespace MediaBrowser.Controller.Entities.TV
{
    public class ScheduleEpisode
    {
        public string Title { get; set; } = string.Empty;

        public string AlternateTitle { get; set; } = string.Empty;

        public int? Number { get; set; }

        public string Overview { get; set; } = string.Empty;

        public DateTime? FirstAiring { get; set; }

        public string ImageUrl { get; set; } = string.Empty;

        public string MagnetUrl { get; set; } = string.Empty;

        public int? Seeders { get; set; }

        public bool Subscribed { get; set; } = false;

        public string AiringStatus { get; set; } = string.Empty;

        public bool HasDub { get; set; } = false;

        public int? DubNumber { get; set; }

        public int Season { get; set; } = 1;

        public string[] Genres { get; set; } = [];
    }
}
