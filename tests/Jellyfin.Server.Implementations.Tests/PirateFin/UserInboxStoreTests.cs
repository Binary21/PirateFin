using System;
using System.IO;
using Emby.Server.Implementations.PirateFin.Notifications;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// The bell: notifications are kept per user, newest first, until the user deletes them.
/// </summary>
public sealed class UserInboxStoreTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "piratefin-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();
    private readonly UserInboxStore _store;

    public UserInboxStoreTests()
    {
        _store = New();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    private UserInboxStore New()
        => new(Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath), TimeProvider.System, NullLogger<UserInboxStore>.Instance);

    [Fact]
    public void Notifications_AreNewestFirst_Unseen_AndKeptAcrossRestarts()
    {
        _store.Add(_user, UserNotificationKind.EpisodeReady, "first", null, null);
        _store.Add(_user, UserNotificationKind.RequestClosed, "second", "note", Guid.NewGuid());

        var reloaded = New().Get(_user);

        Assert.Equal(["second", "first"], [reloaded[0].Title, reloaded[1].Title]);
        Assert.All(reloaded, n => Assert.False(n.Seen));
        Assert.Empty(_store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void OpeningTheBell_MarksAllSeen()
    {
        _store.Add(_user, UserNotificationKind.EpisodeReady, "a", null, null);

        _store.MarkAllSeen(_user);

        Assert.All(_store.Get(_user), n => Assert.True(n.Seen));
    }

    [Fact]
    public void Delete_RemovesOnlyThatNotification_AndClearRemovesAll()
    {
        _store.Add(_user, UserNotificationKind.EpisodeReady, "keep", null, null);
        _store.Add(_user, UserNotificationKind.EpisodeReady, "remove", null, null);
        var remove = _store.Get(_user)[0];

        Assert.True(_store.Delete(_user, remove.Id));
        Assert.False(_store.Delete(Guid.NewGuid(), _store.Get(_user)[0].Id));
        Assert.Equal("keep", Assert.Single(_store.Get(_user)).Title);

        _store.Clear(_user);
        Assert.Empty(_store.Get(_user));
    }

    [Fact]
    public void OnlyTheNewestAreKept()
    {
        for (var i = 0; i <= UserInboxStore.MaxPerUser; i++)
        {
            _store.Add(_user, UserNotificationKind.EpisodeReady, i.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null);
        }

        var inbox = _store.Get(_user);
        Assert.Equal(UserInboxStore.MaxPerUser, inbox.Count);
        Assert.Equal(UserInboxStore.MaxPerUser.ToString(System.Globalization.CultureInfo.InvariantCulture), inbox[0].Title);
    }
}
