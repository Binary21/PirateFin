using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Torrents;

/// <summary>
/// Jackett torznab client.
/// </summary>
public class JackettClient : IDisposable
{
    private static readonly XNamespace _torznab = "http://torznab.com/schemas/2015/feed";

    // Every episode search fans out into several queries; keep Jackett / the indexers from being flooded.
    private readonly SemaphoreSlim _concurrency = new(4, 4);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<JackettOptions> _options;
    private readonly ILogger<JackettClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JackettClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="options">The Jackett options.</param>
    /// <param name="logger">The logger.</param>
    public JackettClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<JackettOptions> options, ILogger<JackettClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Runs a search. Failures are logged and return no results so one bad indexer can't abort a search.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="indexer">The Jackett indexer id.</param>
    /// <param name="category">The torznab category.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The releases that have a magnet / torrent url.</returns>
    public virtual async Task<IReadOnlyList<TorrentCandidate>> SearchAsync(string query, string indexer, string category, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(indexer))
        {
            return [];
        }

        var options = _options.CurrentValue;
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{options.BaseUrl.TrimEnd('/')}/api/v2.0/indexers/{Uri.EscapeDataString(indexer)}/results/torznab/api?apikey={Uri.EscapeDataString(options.ApiKey)}&t=search&q={Uri.EscapeDataString(query)}&cat={Uri.EscapeDataString(category)}&limit=100&cache=false");

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
                return Parse(document);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Jackett search on {Indexer} failed for '{Query}'", indexer, query);
            return [];
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Parses a torznab feed.
    /// </summary>
    /// <param name="document">The feed.</param>
    /// <returns>The releases.</returns>
    internal static IReadOnlyList<TorrentCandidate> Parse(XDocument document)
    {
        var results = new List<TorrentCandidate>();
        foreach (var item in document.Descendants("item"))
        {
            var magnet = GetMagnetUrl(item);
            if (string.IsNullOrWhiteSpace(magnet))
            {
                continue;
            }

            var title = item.Element("title")?.Value.Trim() ?? string.Empty;
            var info = ReleaseTitleParser.Parse(title);
            results.Add(new TorrentCandidate
            {
                Title = title,
                MagnetUrl = magnet,
                Seeders = (int)GetAttribute(item, "seeders"),
                SizeBytes = long.TryParse(item.Element("size")?.Value, CultureInfo.InvariantCulture, out var size) ? size : GetAttribute(item, "size"),
                Season = info.Season,
                Episode = info.Episode
            });
        }

        return results;
    }

    /// <summary>
    /// Releases resources.
    /// </summary>
    /// <param name="disposing">Whether called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _concurrency.Dispose();
        }
    }

    private static string GetMagnetUrl(XElement item)
    {
        var enclosure = item.Element("enclosure")?.Attribute("url")?.Value;
        var candidates = new[] { enclosure, item.Element("link")?.Value, item.Element("guid")?.Value, GetAttributeValue(item, "magneturl") };
        return candidates.FirstOrDefault(IsMagnet) ?? enclosure ?? string.Empty;
    }

    private static bool IsMagnet(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);

    private static long GetAttribute(XElement item, string name)
        => long.TryParse(GetAttributeValue(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? GetAttributeValue(XElement item, string name)
        => item.Elements(_torznab + "attr")
            .FirstOrDefault(e => string.Equals(e.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;
}
