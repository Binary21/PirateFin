using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Notifications;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Subscribers are emailed about episodes that aired after they subscribed, never about the back catalogue; episodes of
/// a series that download together get one email.
/// </summary>
public sealed class NewEpisodeNotifierTests : IDisposable
{
    private static readonly DateTime _aired = new(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "piratefin-episode-emails-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ISeasonSubscriptions> _subscriptions = new();
    private readonly Mock<IUserNotificationSettings> _settings = new();
    private readonly Mock<IUserInbox> _inbox = new();
    private readonly Mock<IEmailSender> _emailSender = new();
    private readonly Mock<IDownloadManager> _downloadManager = new();
    private readonly List<Guid> _pending = [];
    private readonly List<PirateFinEmail> _sent = [];
    private readonly Season _season = new() { Id = Guid.NewGuid() };
    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly User _user = new("viewer", "default", "default");
    private readonly NewEpisodeNotifier _notifier;

    public NewEpisodeNotifierTests()
    {
        _libraryManager.Setup(l => l.GetItemById(_season.Id)).Returns(_season);
        _userManager.Setup(u => u.GetUserById(_user.Id)).Returns(_user);
        _emailSender.Setup(e => e.IsConfigured).Returns(true);
        _emailSender.Setup(e => e.SendAsync(It.IsAny<string>(), It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()))
            .Callback((string _, PirateFinEmail email, CancellationToken _) => _sent.Add(email))
            .Returns(Task.CompletedTask);
        _settings.Setup(s => s.Get(_user.Id)).Returns(new NotificationSettings { Email = "viewer@example.com", NewEpisodeEmails = true });
        _downloadManager.Setup(d => d.GetPendingDownloads()).Returns(() => _pending.ToList());
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);

        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration { ServerName = "Home" });

        _notifier = new NewEpisodeNotifier(
            _downloadManager.Object,
            _libraryManager.Object,
            _userManager.Object,
            _subscriptions.Object,
            _settings.Object,
            _inbox.Object,
            _emailSender.Object,
            Mock.Of<IEmailAssets>(),
            new EpisodeEmailBatches(Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath), NullLogger<EpisodeEmailBatches>.Instance),
            configuration.Object,
            NullLogger<NewEpisodeNotifier>.Instance);
    }

    public void Dispose()
    {
        _notifier.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    private Episode Episode(int number = 5, string name = "The Return")
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = name,
            SeriesName = "My Show",
            SeriesId = _seriesId,
            ParentIndexNumber = 2,
            IndexNumber = number,
            SeasonId = _season.Id,
            FirstAiring = _aired
        };
        _libraryManager.Setup(l => l.GetItemById(episode.Id)).Returns(episode);
        return episode;
    }

    private void SubscribedAt(DateTime since)
        => _subscriptions.Setup(s => s.GetSubscribers(_season.Id)).Returns([new SeasonSubscriber(_user.Id, since)]);

    private void Downloading(params Episode[] episodes) => _pending.AddRange(episodes.Select(e => e.Id));

    private async Task CompleteAsync(Episode episode)
    {
        _pending.Remove(episode.Id);
        await _notifier.NotifyAsync(episode, CancellationToken.None);
    }

    [Fact]
    public async Task EpisodeAiredAfterSubscribing_IsEmailed()
    {
        SubscribedAt(_aired.AddDays(-7));

        await _notifier.NotifyAsync(Episode(), CancellationToken.None);

        var email = Assert.Single(_sent);
        Assert.Equal("My Show S02E05 is ready to watch", email.Subject);
        Assert.Contains("S02E05 · The Return", email.Text, StringComparison.Ordinal);
        Assert.Contains("The Return", email.Html, StringComparison.Ordinal);
        Assert.Contains("cid:piratefin-logo", email.Html, StringComparison.Ordinal);
        Assert.Contains(email.Images, i => i.ContentId == "piratefin-logo" && i.Data.Length > 0);
    }

    [Fact]
    public async Task BackCatalogue_IsNotEmailed()
    {
        SubscribedAt(_aired.AddDays(3));

        await _notifier.NotifyAsync(Episode(), CancellationToken.None);

        Assert.Empty(_sent);
        _inbox.Verify(i => i.Add(It.IsAny<Guid>(), It.IsAny<UserNotificationKind>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Theory]
    [InlineData("viewer@example.com", false)]
    [InlineData(null, true)]
    public async Task EmailsOffOrNoAddress_IsNotEmailed(string? email, bool newEpisodeEmails)
    {
        SubscribedAt(_aired.AddDays(-7));
        _settings.Setup(s => s.Get(_user.Id)).Returns(new NotificationSettings { Email = email, NewEpisodeEmails = newEpisodeEmails });

        await _notifier.NotifyAsync(Episode(), CancellationToken.None);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task EmailsOffOrNotSetUp_StillNotifiesInTheBell()
    {
        SubscribedAt(_aired.AddDays(-7));
        _emailSender.Setup(e => e.IsConfigured).Returns(false);
        _settings.Setup(s => s.Get(_user.Id)).Returns(new NotificationSettings { NewEpisodeEmails = false });
        var episode = Episode();

        await _notifier.NotifyAsync(episode, CancellationToken.None);

        _inbox.Verify(i => i.Add(_user.Id, UserNotificationKind.EpisodeReady, "My Show S02E05 is ready to watch", "The Return", episode.Id), Times.Once);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task EpisodesDownloadingTogether_GetOneEmail_WhenTheFirstIsReady()
    {
        SubscribedAt(_aired.AddDays(-7));
        var first = Episode(3, "Three");
        var second = Episode(4, "Four");
        var third = Episode(5, "Five");
        Downloading(first, second, third);

        await CompleteAsync(first);
        await CompleteAsync(third);
        await CompleteAsync(second);

        var email = Assert.Single(_sent);
        Assert.Equal("3 new episodes of My Show", email.Subject);
        Assert.Contains("S02E03  Three", email.Text, StringComparison.Ordinal);
        Assert.Contains("S02E05  Five", email.Text, StringComparison.Ordinal);

        // The bell still gets each one.
        _inbox.Verify(i => i.Add(_user.Id, UserNotificationKind.EpisodeReady, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Guid?>()), Times.Exactly(3));
    }

    [Fact]
    public async Task LaterEpisodeFinishingFirst_WaitsForTheFirstOne()
    {
        SubscribedAt(_aired.AddDays(-7));
        var first = Episode(3, "Three");
        var second = Episode(4, "Four");
        Downloading(first, second);

        await CompleteAsync(second);
        Assert.Empty(_sent);

        await CompleteAsync(first);
        var email = Assert.Single(_sent);
        Assert.Equal("2 new episodes of My Show", email.Subject);
        Assert.Contains("S02E04  Four", email.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WholeSeason_IsAnnouncedAsDownloaded()
    {
        SubscribedAt(_aired.AddDays(-7));
        var episodes = new[] { Episode(1, "One"), Episode(2, "Two") };
        _libraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(_season.Id)))).Returns(episodes.Cast<BaseItem>().ToList());
        Downloading(episodes);

        await CompleteAsync(episodes[0]);

        var email = Assert.Single(_sent);
        Assert.Equal("Season 2 of My Show was downloaded", email.Subject);
    }
}
