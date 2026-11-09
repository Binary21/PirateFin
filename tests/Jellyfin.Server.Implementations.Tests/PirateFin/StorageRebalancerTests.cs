using System;
using System.IO;
using System.Linq;
using System.Threading;
using Emby.Server.Implementations.PirateFin.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// A series split across two drives is moved back onto one, keeping its library path.
/// </summary>
/// <remarks>
/// Needs two fixed drives (it writes a few bytes to the second one and to "&lt;drive&gt;\PirateFin Storage"), so it only
/// runs when the PIRATEFIN_DRIVE_TESTS environment variable is "1".
/// </remarks>
public sealed class StorageRebalancerTests : IDisposable
{
    private readonly string? _firstRoot;
    private readonly string? _secondRoot;

    public StorageRebalancerTests()
    {
        if (Environment.GetEnvironmentVariable("PIRATEFIN_DRIVE_TESTS") != "1" || !OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDrive = Path.GetPathRoot(Path.GetTempPath())!;
        var other = DriveInfo.GetDrives()
            .FirstOrDefault(d => d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.Name, tempDrive, StringComparison.OrdinalIgnoreCase));
        if (other is null)
        {
            return;
        }

        var name = "pf-rebalancer-" + Guid.NewGuid().ToString("N");
        _firstRoot = Path.Combine(Path.GetTempPath(), name);
        _secondRoot = Path.Combine(other.RootDirectory.FullName, name);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _firstRoot, _secondRoot }.OfType<string>())
        {
            if (Directory.Exists(root))
            {
                // Junctions first, so deleting the tree never follows one.
                foreach (var link in new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.AllDirectories).Where(d => d.LinkTarget is not null).ToList())
                {
                    link.Delete();
                }

                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void SplitSeries_IsMovedOntoOneDrive()
    {
        if (_firstRoot is null || _secondRoot is null)
        {
            return;
        }

        var firstLocation = Path.Combine(_firstRoot, "Shows");
        var secondLocation = Path.Combine(_secondRoot, "Shows");
        var series = Path.Combine(firstLocation, "My Show");
        var overflowSeason = Path.Combine(_secondRoot, "overflow", "Season 2");

        Directory.CreateDirectory(Path.Combine(series, "Season 1"));
        Directory.CreateDirectory(secondLocation);
        Directory.CreateDirectory(overflowSeason);
        File.WriteAllText(Path.Combine(series, "Season 1", "My Show - S01E01.mkv"), "one");
        File.WriteAllText(Path.Combine(overflowSeason, "My Show - S02E01.mkv"), "two");
        LibraryStorage.CreateJunction(Path.Combine(series, "Season 2"), overflowSeason);

        var rebalancer = CreateRebalancer(firstLocation, secondLocation);
        Assert.Single(rebalancer.GetSplitSeries());

        rebalancer.ReuniteSplitSeries(CancellationToken.None);

        // Same library paths, same content, one drive.
        Assert.Equal("one", File.ReadAllText(Path.Combine(series, "Season 1", "My Show - S01E01.mkv")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(series, "Season 2", "My Show - S02E01.mkv")));
        var drives = new[] { Path.Combine(series, "Season 1"), Path.Combine(series, "Season 2") }
            .Select(p => LibraryStorage.GetDrive(p)!.Name)
            .Distinct()
            .ToList();
        var drive = Assert.Single(drives);
        Assert.Empty(rebalancer.GetSplitSeries());

        // Nothing is left in the staging folder of the drive it moved to.
        var staging = Path.Combine(drive, LibraryStorage.OverflowFolderName, ".staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any(), $"Leftovers in {staging}");
        Console.WriteLine($"Moved onto {drive}; staging folder exists afterwards: {Directory.Exists(staging)}");
    }

    private static StorageRebalancer CreateRebalancer(string firstLocation, string secondLocation)
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Name = "Shows", Locations = [firstLocation, secondLocation] }]);

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(s => s.Sessions).Returns([]);

        var options = new Mock<IOptionsMonitor<QBitTorrentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new QBitTorrentOptions
        {
            MinimumFreeSpaceGb = 0,
            StorageMoveStart = "00:00",
            StorageMoveEnd = "23:59:59"
        });

        var storage = new LibraryStorage(libraryManager.Object, options.Object, NullLogger<LibraryStorage>.Instance);
        return new StorageRebalancer(
            libraryManager.Object,
            Mock.Of<ILibraryMonitor>(),
            sessionManager.Object,
            storage,
            Mock.Of<IDownloadManager>(),
            options.Object,
            TimeProvider.System,
            NullLogger<StorageRebalancer>.Instance);
    }
}
