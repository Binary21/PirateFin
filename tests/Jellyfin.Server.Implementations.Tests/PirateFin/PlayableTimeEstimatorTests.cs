using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Downloads;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Finding how far a downloading video plays without a gap, from its keyframes' byte positions.
/// </summary>
public class PlayableTimeEstimatorTests
{
    // Measured on a real 1431 s, 1485 MB episode: bytes don't spread evenly over time.
    private static readonly (double Seconds, long Position)[] _keyframes =
    [
        (0, 0),
        (118.1, 146_825_979),
        (298.3, 370_134_768),
        (478.5, 578_482_956),
        (598.6, 717_058_668),
        (718.7, 818_622_282),
        (838.8, 894_148_800),
        (964.2, 1_019_644_611),
        (1246.5, 1_260_833_650),
        (1431.5, 1_484_866_183)
    ];

    // A lookup that finds the keyframe at or before a time; past the edge the file is empty and ffprobe fails.
    private static Func<double, CancellationToken, Task<(double Seconds, long Position)?>> Lookup(long edgeBytes)
        => (t, _) =>
        {
            var keyframe = _keyframes.Last(k => k.Seconds <= t);
            return Task.FromResult<(double, long)?>(keyframe.Position < edgeBytes ? keyframe : null);
        };

    [Fact]
    public async Task FindsTheLastKeyframeBeforeTheEdge()
    {
        // 48% of the bytes downloaded without a gap: an even spread would say 691 s, but the data ends near 599 s.
        const long Edge = 717_058_669;
        var seconds = await PlayableTimeEstimator.SearchAsync(Lookup(Edge), Edge, 0, 1431.5, CancellationToken.None);

        Assert.Equal(598.6, seconds);
    }

    [Fact]
    public async Task NeverGoesBackFromAnEarlierEstimate()
    {
        const long Edge = 894_148_801;
        var seconds = await PlayableTimeEstimator.SearchAsync(Lookup(Edge), Edge, 598.6, 1431.5, CancellationToken.None);

        Assert.Equal(838.8, seconds);
    }

    [Fact]
    public async Task UnknownWhenNoLookupWorks()
    {
        var seconds = await PlayableTimeEstimator.SearchAsync((_, _) => Task.FromResult<(double, long)?>(null), 1000, 0, 1431.5, CancellationToken.None);

        Assert.Null(seconds);
    }

    [Theory]
    [InlineData("598.599000,717058668\r\n", 598.599, 717058668L)]
    [InlineData("\n118.118000,146825979\n", 118.118, 146825979L)]
    public void ReadsFfprobeOutput(string output, double seconds, long position)
        => Assert.Equal((seconds, position), PlayableTimeEstimator.Parse(output));

    [Fact]
    public void NothingUsableInFfprobeOutput()
        => Assert.Null(PlayableTimeEstimator.Parse("N/A,N/A\n"));
}
