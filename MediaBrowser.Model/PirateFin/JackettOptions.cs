using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MediaBrowser.Model.PirateFin;

/// <summary>
/// Jackett settings, loaded from the "Jackett" section of jackett.json.
/// </summary>
public sealed class JackettOptions
{
    /// <summary>
    /// The name of the configuration section.
    /// </summary>
    public const string SectionName = "Jackett";

    /// <summary>
    /// Gets or sets the Jackett base url.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:9117/";

    /// <summary>
    /// Gets or sets the Jackett API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the indexer to search, keyed by library name.
    /// </summary>
    public Dictionary<string, string> Indexer { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the torznab category for anime.
    /// </summary>
    public string AnimeCategory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the torznab category for TV shows.
    /// </summary>
    public string ShowCategory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the old single list of preferred anime release authors. Replaced by <see cref="AnimeSubGroups"/>
    /// and <see cref="AnimeDubGroups"/>; read only to migrate an older jackett.json (see <see cref="MigrateAnimeAuthors"/>).
    /// </summary>
    public string[] AnimeAuthors { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the preferred anime dub release groups, in priority order.
    /// </summary>
    public ReleaseGroup[] AnimeDubGroups { get; set; } = Array.Empty<ReleaseGroup>();

    /// <summary>
    /// Gets or sets the preferred anime sub release groups, in priority order.
    /// </summary>
    public ReleaseGroup[] AnimeSubGroups { get; set; } = Array.Empty<ReleaseGroup>();

    /// <summary>
    /// Gets or sets a value indicating whether dubs come first: "Dub if available" is preselected when subscribing, and
    /// downloads nobody chose a language for (manual downloads, prefetch, older subscriptions) look for a dub first.
    /// </summary>
    public bool PrioritizeDub { get; set; }

    /// <summary>
    /// Gets or sets the preferred TV release authors, in priority order.
    /// </summary>
    public string[] ShowAuthors { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets the language a download defaults to when no user chose one.
    /// </summary>
    [JsonIgnore]
    public ReleaseLanguage DefaultLanguage => PrioritizeDub ? ReleaseLanguage.Dub : ReleaseLanguage.Sub;

    /// <summary>
    /// Gets the preferred anime groups for a language.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <returns>The groups, in priority order.</returns>
    public IReadOnlyList<ReleaseGroup> GetAnimeGroups(ReleaseLanguage language)
        => language == ReleaseLanguage.Dub ? AnimeDubGroups : AnimeSubGroups;

    /// <summary>
    /// Moves the old <see cref="AnimeAuthors"/> list into <see cref="AnimeSubGroups"/> (same order, no indicators) when
    /// the sub list is still empty.
    /// </summary>
    public void MigrateAnimeAuthors()
    {
        var authors = AnimeAuthors.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        if (authors.Length > 0 && AnimeSubGroups.Length == 0)
        {
            AnimeSubGroups = authors.Select(a => new ReleaseGroup { Name = a.Trim() }).ToArray();
        }

        AnimeAuthors = Array.Empty<string>();
    }
}
