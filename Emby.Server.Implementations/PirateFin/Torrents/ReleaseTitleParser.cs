using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Server.Implementations.PirateFin.Torrents;

/// <summary>
/// Parses season / episode numbers out of torrent release titles, covering both scene ("Show.S01E05.1080p")
/// and fansub ("[Group] Show - 05 (1080p)") naming.
/// </summary>
public static partial class ReleaseTitleParser
{
    /// <summary>
    /// Parses a release title.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns>The parsed info.</returns>
    public static ReleaseInfo Parse(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return default;
        }

        if (BatchRegex().IsMatch(title) || MultiEpisodeRegex().IsMatch(title) || HasEpisodeRange(title))
        {
            return new ReleaseInfo(ParseSeason(title), null, true);
        }

        var match = SeasonEpisodeRegex().Match(title);
        if (match.Success)
        {
            return new ReleaseInfo(ToInt(match.Groups["season"]), ToInt(match.Groups["episode"]), false);
        }

        match = CrossEpisodeRegex().Match(title);
        if (match.Success)
        {
            return new ReleaseInfo(ToInt(match.Groups["season"]), ToInt(match.Groups["episode"]), false);
        }

        var season = ParseSeason(title);
        match = DashEpisodeRegex().Match(title);
        if (!match.Success)
        {
            match = EpisodeWordRegex().Match(title);
        }

        if (match.Success)
        {
            return new ReleaseInfo(season, ToInt(match.Groups["episode"]), false);
        }

        // "Show S01 1080p" with no episode is a season pack.
        return new ReleaseInfo(season, null, season.HasValue);
    }

    /// <summary>
    /// Gets a value indicating whether a release is exactly the requested episode.
    /// </summary>
    /// <param name="info">The parsed release.</param>
    /// <param name="season">The season number.</param>
    /// <param name="episode">The episode number.</param>
    /// <returns>Whether it matches. Fansub releases usually omit season 1, so a missing season matches.</returns>
    public static bool IsEpisode(ReleaseInfo info, int season, int episode)
        => !info.IsPack
            && info.Episode == episode
            && (info.Season is null || info.Season == season);

    /// <summary>
    /// Gets a value indicating whether a release is clearly something other than the requested episode
    /// (another episode, another season, or a pack).
    /// </summary>
    /// <param name="info">The parsed release.</param>
    /// <param name="season">The season number.</param>
    /// <param name="episode">The episode number.</param>
    /// <returns>Whether it should be excluded.</returns>
    public static bool IsOtherContent(ReleaseInfo info, int season, int episode)
        => info.IsPack
            || (info.Episode.HasValue && info.Episode != episode)
            || (info.Season.HasValue && info.Season != season);

    /// <summary>
    /// Gets a value indicating whether every word of <paramref name="author"/> appears in the release title,
    /// e.g. "[SubsPlease]" or "1080p MeGusta" (scene groups put their name at the end: "...x265-MeGusta").
    /// </summary>
    /// <param name="releaseTitle">The release title.</param>
    /// <param name="author">The author.</param>
    /// <returns>Whether the release is by the author.</returns>
    public static bool IsByAuthor(string releaseTitle, string author)
    {
        var words = Words(releaseTitle);
        var authorWords = Words(author);
        return authorWords.Length > 0 && authorWords.All(w => words.Contains(w, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a value indicating whether the release title mentions most of the words of the series title,
    /// guarding against indexers that return loosely related results.
    /// </summary>
    /// <param name="releaseTitle">The release title.</param>
    /// <param name="seriesTitle">The series title that was searched.</param>
    /// <returns>Whether the release looks like it belongs to the series.</returns>
    public static bool MentionsTitle(string releaseTitle, string seriesTitle)
    {
        var titleWords = Words(seriesTitle).Where(w => w.Length > 1).ToArray();
        if (titleWords.Length == 0)
        {
            return true;
        }

        var words = Words(releaseTitle);
        var found = titleWords.Count(w => words.Contains(w, StringComparer.OrdinalIgnoreCase));
        return found * 2 >= titleWords.Length;
    }

    /// <summary>
    /// Strips everything but letters, digits and spaces, as indexer search does.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The cleaned value.</returns>
    public static string CleanQueryText(string value)
        => WhitespaceRegex().Replace(NonWordRegex().Replace(value, " "), " ").Trim();

    /// <summary>
    /// Gets the BitTorrent info hash from a magnet url.
    /// </summary>
    /// <param name="magnetUrl">The magnet url.</param>
    /// <returns>The upper-case hash, or an empty string.</returns>
    public static string GetInfoHash(string magnetUrl)
    {
        var match = InfoHashRegex().Match(magnetUrl ?? string.Empty);
        return match.Success ? match.Groups["hash"].Value.ToUpperInvariant() : string.Empty;
    }

    // "(01-28)" / "01 ~ 12" are ranges; "Mob Psycho 100 - 05" and dates are not, because the range must increase.
    private static bool HasEpisodeRange(string title)
    {
        foreach (Match match in EpisodeRangeRegex().Matches(title))
        {
            if (ToInt(match.Groups["from"]) is { } from && ToInt(match.Groups["to"]) is { } to && to > from)
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Words(string value)
        => NonWordRegex().Split(value).Where(w => w.Length > 0).ToArray();

    private static int? ParseSeason(string title)
    {
        var match = SeasonOnlyRegex().Match(title);
        return match.Success ? ToInt(match.Groups["season"]) : null;
    }

    private static int? ToInt(Group group)
        => group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    [GeneratedRegex(@"\bS(?<season>\d{1,2})[\s._-]*E(?<episode>\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\b(?<season>\d{1,2})x(?<episode>\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CrossEpisodeRegex();

    // "[Group] Show - 05 (1080p)", "Show - 05v2 [ABCD1234].mkv"
    [GeneratedRegex(@"\s-\s(?<episode>\d{1,4})(?:v\d)?(?=$|[\s\[\(.])")]
    private static partial Regex DashEpisodeRegex();

    [GeneratedRegex(@"\b(?:EP?|Episode)\s?(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeWordRegex();

    [GeneratedRegex(@"\b(?:S(?<season>\d{1,2})|Season\s?(?<season>\d{1,2})|(?<season>\d{1,2})(?:st|nd|rd|th)\s+Season)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonOnlyRegex();

    [GeneratedRegex(@"\b(?:batch|complete|season\s?pack)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BatchRegex();

    // "01-12", "(01 ~ 12)"; the numbers must stand alone, which excludes "S2 - 03", "x265-GROUP" and dates.
    [GeneratedRegex(@"(?<![\w.\-])(?<from>\d{2,4})\s?[-~]\s?(?<to>\d{2,4})(?![\dp\-])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRangeRegex();

    // "S01E01-E10", "S01E01-10"
    [GeneratedRegex(@"\bS\d{1,2}E\d{1,4}\s?-\s?E?\d{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex MultiEpisodeRegex();

    [GeneratedRegex(@"btih:(?<hash>[a-z0-9]{32,40})", RegexOptions.IgnoreCase)]
    private static partial Regex InfoHashRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
