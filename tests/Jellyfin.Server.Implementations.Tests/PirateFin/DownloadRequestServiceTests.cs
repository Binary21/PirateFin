using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Notifications;
using Emby.Server.Implementations.PirateFin.Requests;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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
/// Users can request episodes PirateFin gave up on, up to their limit; admins hear about them, users about the outcome.
/// </summary>
public sealed class DownloadRequestServiceTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "piratefin-requests-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IEmailSender> _emailSender = new();
    private readonly Mock<IUserNotificationSettings> _settings = new();
    private readonly Mock<IUserInbox> _inbox = new();
    private readonly User _viewer = new("viewer", "default", "default");
    private readonly User _admin = new("admin", "default", "default");
    private readonly DownloadRequestService _service;

    public DownloadRequestServiceTests()
    {
        _admin.SetPermission(PermissionKind.IsAdministrator, true);
        _userManager.Setup(u => u.GetUsers()).Returns([_viewer, _admin]);
        _emailSender.Setup(e => e.IsConfigured).Returns(true);
        _settings.Setup(s => s.Get(_viewer.Id)).Returns(new NotificationSettings { Email = "viewer@example.com" });
        _settings.Setup(s => s.Get(_admin.Id)).Returns(new NotificationSettings { Email = "admin@example.com" });

        var appPaths = Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath);
        _service = new DownloadRequestService(
            appPaths,
            Mock.Of<ILibraryManager>(),
            _userManager.Object,
            Mock.Of<IDownloadManager>(),
            _emailSender.Object,
            Mock.Of<IEmailAssets>(),
            _settings.Object,
            _inbox.Object,
            TimeProvider.System,
            NullLogger<DownloadRequestService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    // An episode PirateFin gave up on: aired, no release and no fallback found, no longer searched.
    private static Episode GaveUp(int number) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Episode " + number,
        SeriesName = "My Show",
        ParentIndexNumber = 1,
        IndexNumber = number,
        IsInternet = true,
        IsDownloaded = false,
        Scheduled = false,
        FirstAiring = DateTime.UtcNow.AddDays(-3)
    };

    [Fact]
    public async Task Request_EmailsTheAdmins()
    {
        var request = await _service.CreateAsync(_viewer, GaveUp(5), CancellationToken.None);

        Assert.Equal("S01E05: Episode 5", request.Title);
        Assert.Equal(DownloadRequestStatus.Open, request.Status);
        _emailSender.Verify(
            e => e.SendAsync("admin@example.com", It.Is<PirateFinEmail>(m => m.Subject == "Download request: My Show S01E05: Episode 5" && m.Text.Contains("Asked for by viewer", StringComparison.Ordinal)), It.IsAny<CancellationToken>()),
            Times.Once);
        _emailSender.Verify(e => e.SendAsync("viewer@example.com", It.IsAny<PirateFinEmail>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EpisodeStillBeingSearched_CantBeRequested()
    {
        var episode = GaveUp(5);
        episode.Scheduled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(_viewer, episode, CancellationToken.None));
    }

    [Fact]
    public async Task SameItemTwice_IsRefused()
    {
        var episode = GaveUp(5);
        await _service.CreateAsync(_viewer, episode, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(_viewer, episode, CancellationToken.None));
    }

    [Fact]
    public async Task Limit_IsThreeOpenRequests_AndCancellingFreesASlot()
    {
        var first = await _service.CreateAsync(_viewer, GaveUp(1), CancellationToken.None);
        await _service.CreateAsync(_viewer, GaveUp(2), CancellationToken.None);
        await _service.CreateAsync(_viewer, GaveUp(3), CancellationToken.None);

        Assert.Equal(new RequestQuota(3, 3), _service.GetQuota(_viewer.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(_viewer, GaveUp(4), CancellationToken.None));

        Assert.True(_service.Cancel(first.Id, _viewer.Id));
        Assert.Equal(1, _service.GetQuota(_viewer.Id).Remaining);
        await _service.CreateAsync(_viewer, GaveUp(4), CancellationToken.None);
    }

    [Fact]
    public async Task UnlimitedUser_HasNoLimit()
    {
        _service.SetLimit(_viewer.Id, -1);

        for (var i = 1; i <= 5; i++)
        {
            await _service.CreateAsync(_viewer, GaveUp(i), CancellationToken.None);
        }

        Assert.Equal(-1, _service.GetQuota(_viewer.Id).Remaining);
    }

    [Fact]
    public async Task Closing_FreesTheSlot_AndEmailsTheUser()
    {
        var request = await _service.CreateAsync(_viewer, GaveUp(5), CancellationToken.None);

        Assert.True(await _service.CloseAsync(request.Id, DownloadRequestStatus.NotFound, "Not on any tracker yet.", CancellationToken.None));

        Assert.Equal(0, _service.GetQuota(_viewer.Id).Open);
        Assert.Equal(0, _service.CountOpen());
        _emailSender.Verify(
            e => e.SendAsync("viewer@example.com", It.Is<PirateFinEmail>(m => m.Subject == "Not found: My Show S01E05: Episode 5" && m.Text.Contains("Not on any tracker yet.", StringComparison.Ordinal)), It.IsAny<CancellationToken>()),
            Times.Once);
        _inbox.Verify(
            i => i.Add(
                _viewer.Id,
                UserNotificationKind.RequestClosed,
                "Not found: My Show S01E05: Episode 5",
                It.Is<string?>(t => t != null && t.Contains("Note from the admin: Not on any tracker yet.", StringComparison.Ordinal)),
                request.ItemId),
            Times.Once);
    }

    [Fact]
    public async Task OtherUsersRequest_CantBeCancelled()
    {
        var request = await _service.CreateAsync(_viewer, GaveUp(5), CancellationToken.None);

        Assert.False(_service.Cancel(request.Id, _admin.Id));
        Assert.Equal(1, _service.CountOpen());
    }
}
