using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin.Subscriptions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Season subscriptions are per user and survive restarts; the old shared subscriptions go to the admins once.
/// </summary>
/// <remarks>
/// The seasons are already marked subscribed (and keep a subscriber), so nothing is saved to the library.
/// </remarks>
public sealed class SeasonSubscriptionStoreTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "piratefin-subs-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IApplicationPaths> _appPaths = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero));
    private readonly Season _season = new() { Id = Guid.NewGuid(), Name = "Season 2", Subscribed = true };
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public SeasonSubscriptionStoreTests()
    {
        _appPaths.Setup(p => p.DataPath).Returns(_dataPath);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        _userManager.Setup(u => u.GetUsers()).Returns([]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    private SeasonSubscriptionStore CreateStore()
        => new(_appPaths.Object, _libraryManager.Object, _userManager.Object, _time, NullLogger<SeasonSubscriptionStore>.Instance);

    [Fact]
    public async Task Subscription_IsPerUser()
    {
        var store = CreateStore();

        Assert.True(await store.SubscribeAsync(_season, _alice, CancellationToken.None));
        Assert.False(await store.SubscribeAsync(_season, _alice, CancellationToken.None));

        Assert.True(store.IsSubscribed(_season.Id, _alice));
        Assert.False(store.IsSubscribed(_season.Id, _bob));
        var subscriber = Assert.Single(store.GetSubscribers(_season.Id));
        Assert.Equal(_alice, subscriber.UserId);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, subscriber.SinceUtc);

        var subscription = Assert.Single(store.GetSubscriptions(_alice));
        Assert.Equal(_season.Id, subscription.SeasonId);
        Assert.Empty(store.GetSubscriptions(_bob));
    }

    [Fact]
    public async Task Language_IsPerUser_SurvivesARestart_AndCanBeChanged()
    {
        var store = CreateStore();
        await store.SubscribeAsync(_season, _alice, ReleaseLanguage.Dub, CancellationToken.None);
        await store.SubscribeAsync(_season, _bob, CancellationToken.None);

        var reloaded = CreateStore();
        Assert.Equal(ReleaseLanguage.Dub, reloaded.GetLanguage(_season.Id, _alice));
        Assert.Null(reloaded.GetLanguage(_season.Id, _bob));
        Assert.Equal(ReleaseLanguage.Dub, reloaded.GetSubscribers(_season.Id).Single(s => s.UserId.Equals(_alice)).Language);

        // Subscribing again changes the language; it isn't a new subscription.
        Assert.False(await reloaded.SubscribeAsync(_season, _alice, ReleaseLanguage.Sub, CancellationToken.None));
        Assert.Equal(ReleaseLanguage.Sub, reloaded.GetLanguage(_season.Id, _alice));

        await reloaded.UnsubscribeAsync(_season, _alice, CancellationToken.None);
        Assert.Null(reloaded.GetLanguage(_season.Id, _alice));
    }

    [Fact]
    public async Task Unsubscribing_IsRemembered_AndOthersKeepTheirs()
    {
        var store = CreateStore();
        await store.SubscribeAsync(_season, _alice, CancellationToken.None);
        await store.SubscribeAsync(_season, _bob, CancellationToken.None);

        await store.UnsubscribeAsync(_season, _alice, CancellationToken.None);

        Assert.False(store.IsSubscribed(_season.Id, _alice));
        Assert.True(store.HasUnsubscribed(_season.Id, _alice));
        Assert.True(store.IsSubscribed(_season.Id, _bob));
        Assert.True(_season.Subscribed);

        // Subscribing again clears the opt-out.
        await store.SubscribeAsync(_season, _alice, CancellationToken.None);
        Assert.False(store.HasUnsubscribed(_season.Id, _alice));
    }

    [Fact]
    public async Task Subscriptions_SurviveARestart()
    {
        await CreateStore().SubscribeAsync(_season, _bob, CancellationToken.None);

        Assert.True(CreateStore().IsSubscribed(_season.Id, _bob));
    }

    [Fact]
    public async Task SharedSubscriptions_GoToTheAdminsOnce()
    {
        var admin = new User("admin", "default", "default");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        var viewer = new User("viewer", "default", "default");
        _userManager.Setup(u => u.GetUsers()).Returns([admin, viewer]);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([_season]);

        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        Assert.True(store.IsSubscribed(_season.Id, admin.Id));
        Assert.False(store.IsSubscribed(_season.Id, viewer.Id));

        // Only once: an admin who unsubscribes afterwards stays unsubscribed after a restart.
        await store.SubscribeAsync(_season, _bob, CancellationToken.None);
        await store.UnsubscribeAsync(_season, admin.Id, CancellationToken.None);
        var restarted = CreateStore();
        await restarted.StartAsync(CancellationToken.None);
        Assert.False(restarted.IsSubscribed(_season.Id, admin.Id));
        Assert.Equal([_bob], restarted.GetSubscribers(_season.Id).Select(s => s.UserId));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
