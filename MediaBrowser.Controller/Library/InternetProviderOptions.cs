using System;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Settings shared by <see cref="InternetScheduleFetcher"/> and <see cref="InternetSearchFetcher"/>, loaded from providers.json.
    /// </summary>
    public class InternetProviderOptions
    {
        /// <summary>
        /// The name of the configuration section.
        /// </summary>
        public const string SectionName = "Providers";

        /// <summary>
        /// Gets or sets the AniList GraphQL endpoint.
        /// </summary>
        public string AniListGraphQlUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the AniList page size.
        /// </summary>
        public int AniListPageSize { get; set; } = 50;

        /// <summary>
        /// Gets or sets the Trakt API base url.
        /// </summary>
        public string TraktApiUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Trakt API key.
        /// </summary>
        public string TraktApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the user agent sent to Trakt.
        /// </summary>
        public string TraktApiUser { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the YTS API base url.
        /// </summary>
        public string YtsApiUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the minimum rating for movies.
        /// </summary>
        public int MovieMinimumRating { get; set; } = 7;

        /// <summary>
        /// Gets or sets the allowed show genres.
        /// </summary>
        public string[] AllowedShowGenres { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the banned show genres.
        /// </summary>
        public string[] BannedShowGenres { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the allowed show networks.
        /// </summary>
        public string[] AllowedShowNetworks { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the allowed movie languages.
        /// </summary>
        public string[] AllowedLanguages { get; set; } = Array.Empty<string>();
    }
}
