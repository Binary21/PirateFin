using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Notifications;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Whoever opened a download that turns out slow is emailed, then gets a bell notification and an email once it can
/// play; fast downloads tell nobody.
/// </summary>
public sealed class SlowDownloadNotifierTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "piratefin-slow-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IDownloadManager> _downloadManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IUserInbox> _inbox = new();
    private readonly Mock<IEmailSender> _emailSender = new();
    private readonly List<PirateFinEmail> _sent = [];
    private readonly User _user = new("viewer", "default", "default");
    private readonly Episode _episode = new() { Id = Guid.NewGuid(), Name = "Streets of Fire", SeriesName = "9-1-1", ParentIndexNumber = 10, IndexNumber = 3 };
    private readonly SlowDownloadNotifier _notifier;

    public SlowDownloadNotifierTests()
    {
        _libraryManager.Setup(l => l.GetItemById(_episode.Id)).Returns(_episode);
        _userManager.Setup(u => u.GetUserById(_user.Id)).Returns(_user);
        _downloadManager.Setup(d => d.GetPendingDownloads()).Returns([_episode.Id]);
        _downloadManager.Setup(d => d.GetTimeLeft(_episode.Id)).Returns(TimeSpan.FromHours(3));
        _emailSender.Setup(e => e.IsConfigured).Returns(true);
        _emailSender.Setup(e => e.SendAsync(It.IsAny<string>(), It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()))
            .Callback((string _, PirateFinEmail email, CancellationToken _) => _sent.Add(email))
            .Returns(Task.CompletedTask);

        // Like the real store: users without settings get empty ones.
        var settings = new Mock<IUserNotificationSettings>();
        settings.Setup(s => s.Get(It.IsAny<Guid>())).Returns(new NotificationSettings());
        settings.Setup(s => s.Get(_user.Id)).Returns(new NotificationSettings { Email = "viewer@example.com" });
        _notifier = new SlowDownloadNotifier(
            _downloadManager.Object,
            _libraryManager.Object,
            _userManager.Object,
            settings.Object,
            _inbox.Object,
            _emailSender.Object,
            Mock.Of<IEmailAssets>(),
            Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath),
            NullLogger<SlowDownloadNotifier>.Instance);
    }

    public void Dispose()
    {
        _notifier.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    [Fact]
    public async Task SlowDownload_EmailsOnce_ThenBellAndEmailWhenPlayable()
    {
        _downloadManager.Setup(d => d.IsSlow(_episode.Id)).Returns(true);
        _notifier.Register(_user.Id, _episode);

        await _notifier.CheckAsync(CancellationToken.None);
        await _notifier.CheckAsync(CancellationToken.None);

        var slow = Assert.Single(_sent);
        Assert.Equal("9-1-1 S10E03 may take a while", slow.Subject);
        Assert.Contains("About 3 hours left", slow.Text, StringComparison.Ordinal);

        await _notifier.NotifyReadyAsync(_episode, CancellationToken.None);

        Assert.Equal(2, _sent.Count);
        Assert.Equal("9-1-1 S10E03 is ready to watch", _sent[1].Subject);
        _inbox.Verify(i => i.Add(_user.Id, UserNotificationKind.DownloadReady, "9-1-1 S10E03 is ready to watch", It.IsAny<string?>(), _episode.Id), Times.Once);
    }

    [Fact]
    public async Task FastDownload_TellsNobody()
    {
        _downloadManager.Setup(d => d.IsSlow(_episode.Id)).Returns(false);
        _notifier.Register(_user.Id, _episode);

        await _notifier.CheckAsync(CancellationToken.None);
        await _notifier.NotifyReadyAsync(_episode, CancellationToken.None);

        Assert.Empty(_sent);
        _inbox.Verify(i => i.Add(It.IsAny<Guid>(), It.IsAny<UserNotificationKind>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task OpenedAfterItTurnedSlow_IsEmailedToo()
    {
        _downloadManager.Setup(d => d.IsSlow(_episode.Id)).Returns(true);
        var other = new User("other", "default", "default");
        _userManager.Setup(u => u.GetUserById(other.Id)).Returns(other);
        _notifier.Register(other.Id, _episode);
        await _notifier.CheckAsync(CancellationToken.None);

        _notifier.Register(_user.Id, _episode);

        // The email to the late one is sent in the background.
        for (var i = 0; i < 50 && _sent.Count == 0; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Single(_sent);
        Assert.Contains("Hi viewer", _sent.Single().Text, StringComparison.Ordinal);
    }
}
