using System;
using System.Collections.Generic;
using System.IO;
using Emby.Server.Implementations.PirateFin.Profiles;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

/// <summary>
/// Profile PINs (4 digits, locked out after 5 wrong tries), the kids flag, and titles removed from Continue Watching
/// coming back when played.
/// </summary>
public sealed class ProfileSettingsStoreTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "piratefin-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly SettableTimeProvider _time = new(new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc));
    private readonly User _user = new("viewer", "default", "default");

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }

    private ProfileSettingsStore CreateStore()
        => new(Mock.Of<IApplicationPaths>(p => p.DataPath == _dataPath), Mock.Of<ISessionManager>(), _time, NullLogger<ProfileSettingsStore>.Instance);

    [Fact]
    public void Pin_IsChecked_AndSurvivesARestart()
    {
        var store = CreateStore();
        Assert.False(store.HasPin(_user.Id));
        Assert.Equal(true, store.VerifyPin(_user.Id, "anything"));

        store.SetPin(_user.Id, "1234");

        var reloaded = CreateStore();
        Assert.True(reloaded.HasPin(_user.Id));
        Assert.Equal(false, reloaded.VerifyPin(_user.Id, "4321"));
        Assert.Equal(true, reloaded.VerifyPin(_user.Id, "1234"));

        reloaded.RemovePin(_user.Id);
        Assert.False(reloaded.HasPin(_user.Id));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12a4")]
    public void SetPin_NotFourDigits_Throws(string pin)
    {
        Assert.Throws<ArgumentException>(() => CreateStore().SetPin(_user.Id, pin));
    }

    [Fact]
    public void VerifyPin_FiveWrongTries_LocksOutForFiveMinutes()
    {
        var store = CreateStore();
        store.SetPin(_user.Id, "1234");

        for (var i = 0; i < ProfileSettingsStore.MaxPinAttempts; i++)
        {
            Assert.Equal(false, store.VerifyPin(_user.Id, "0000"));
        }

        // Even the right PIN is refused while locked out.
        Assert.Null(store.VerifyPin(_user.Id, "1234"));

        _time.Advance(ProfileSettingsStore.LockoutTime);
        Assert.Equal(true, store.VerifyPin(_user.Id, "1234"));
    }

    [Fact]
    public void Kids_IsRemembered()
    {
        var store = CreateStore();
        store.SetKids(_user.Id, true);

        Assert.True(CreateStore().IsKids(_user.Id));
        Assert.False(store.IsKids(Guid.NewGuid()));
    }

    [Fact]
    public void HiddenTitle_ComesBackWhenPlayed()
    {
        var store = CreateStore();
        var series = Guid.NewGuid();
        var movie = Guid.NewGuid();
        store.HideFromContinueWatching(_user.Id, series);
        store.HideFromContinueWatching(_user.Id, movie);
        Assert.Equal(2, store.GetHiddenFromContinueWatching(_user.Id).Count);

        // Playing any episode brings the series back.
        store.OnPlaybackStart(new PlaybackProgressEventArgs { Item = new Episode { Id = Guid.NewGuid(), SeriesId = series }, Users = new List<User> { _user } });
        Assert.Equal([movie], store.GetHiddenFromContinueWatching(_user.Id));

        store.OnPlaybackStart(new PlaybackProgressEventArgs { Item = new Movie { Id = movie }, Users = new List<User> { _user } });
        Assert.Empty(store.GetHiddenFromContinueWatching(_user.Id));
    }

    private sealed class SettableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;

        public override DateTimeOffset GetUtcNow() => new(_utcNow);
    }
}
