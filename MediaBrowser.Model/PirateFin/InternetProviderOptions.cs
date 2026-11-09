using System;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Trakt / AniList / YTS settings, loaded from the "Providers" section of providers.json.
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
    public string AniListGraphQlUrl { get; set; } = "https://graphql.anilist.co";

    /// <summary>
    /// Gets or sets the AniList page size.
    /// </summary>
    public int AniListPageSize { get; set; } = 50;

    /// <summary>
    /// The countries of origin skipped when <see cref="ExcludedAnimeCountries"/> is not configured (donghua).
    /// </summary>
    public static readonly string[] DefaultExcludedAnimeCountries = ["CN"];

    /// <summary>
    /// Gets or sets the AniList countries of origin to skip; <c>null</c> uses <see cref="DefaultExcludedAnimeCountries"/>.
    /// </summary>
    /// <remarks>No initializer: the configuration binder appends to existing arrays, which would duplicate the defaults.</remarks>
    public string[]? ExcludedAnimeCountries { get; set; }

    /// <summary>
    /// Gets or sets the Trakt API base url.
    /// </summary>
    public string TraktApiUrl { get; set; } = "https://api.trakt.tv";

    /// <summary>
    /// Gets or sets the Trakt API key.
    /// </summary>
    public string TraktApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user agent sent to Trakt.
    /// </summary>
    public string TraktApiUser { get; set; } = "PirateFin";

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
