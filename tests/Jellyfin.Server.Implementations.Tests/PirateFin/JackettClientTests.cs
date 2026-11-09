using System.Xml.Linq;
using Emby.Server.Implementations.PirateFin.Torrents;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

public class JackettClientTests
{
    [Fact]
    public void Parse_ReadsMagnetSeedersSizeAndEpisode()
    {
        var feed = XDocument.Parse("""
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel>
                <item>
                  <title>[SubsPlease] Sousou no Frieren - 05 (1080p)</title>
                  <link>http://localhost:9117/dl/nyaasi/?file=x</link>
                  <size>1452334592</size>
                  <torznab:attr name="seeders" value="321" />
                  <torznab:attr name="magneturl" value="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567" />
                </item>
                <item>
                  <title>No link at all</title>
                </item>
              </channel>
            </rss>
            """);

        var result = Assert.Single(JackettClient.Parse(feed));

        Assert.Equal("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", result.MagnetUrl);
        Assert.Equal(321, result.Seeders);
        Assert.Equal(1452334592, result.SizeBytes);
        Assert.Equal(5, result.Episode);
        Assert.Null(result.Season);
    }

    [Fact]
    public void GetHexInfoHash_ConvertsBase32()
    {
        // Base32 "AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQT" == hex 000102030405060708090a0b0c0d0e0f10111213.
        Assert.Equal(
            "000102030405060708090a0b0c0d0e0f10111213",
            QBitTorrentClient.GetHexInfoHash("magnet:?xt=urn:btih:AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQT"));
    }
}
