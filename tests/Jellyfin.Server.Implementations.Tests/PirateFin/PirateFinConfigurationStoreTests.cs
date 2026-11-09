using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.PirateFin;
using Emby.Server.Implementations.PirateFin.Notifications;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.PirateFin;

public sealed class PirateFinConfigurationStoreTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "piratefin-config-" + Guid.NewGuid().ToString("N"));
    private readonly PirateFinConfigurationStore _store;

    public PirateFinConfigurationStoreTests()
    {
        Directory.CreateDirectory(_configDir);
        var paths = Mock.Of<IApplicationPaths>(p => p.ConfigurationDirectoryPath == _configDir);
        _store = new PirateFinConfigurationStore(
            paths,
            Mock.Of<IOptionsMonitor<InternetProviderOptions>>(),
            Mock.Of<IOptionsMonitor<JackettOptions>>(),
            Mock.Of<IOptionsMonitor<QBitTorrentOptions>>(),
            Mock.Of<IOptionsMonitor<GmailOptions>>(m => m.CurrentValue == new GmailOptions()),
            Mock.Of<IOptionsMonitor<TrailerOptions>>(),
            Mock.Of<IOptionsMonitor<YouTubeOptions>>(),
            NullLogger<PirateFinConfigurationStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_configDir, true);

    private T Load<T>(string file, string section)
        where T : new()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(_configDir).AddJsonFile(file).Build();
        var options = new T();
        configuration.GetSection(section).Bind(options);
        return options;
    }

    [Fact]
    public async Task SaveJackett_RoundTripsThroughTheConfigurationBinder()
    {
        var options = new JackettOptions
        {
            BaseUrl = "http://jackett:9117/",
            ApiKey = "key",
            ShowAuthors = ["1080p MeGusta"],
            AnimeAuthors = ["[SubsPlease]", "[Erai-raws]"]
        };
        options.Indexer["Shows"] = "knaben";

        await _store.SaveJackettAsync(options, CancellationToken.None);
        var loaded = Load<JackettOptions>(PirateFinConfigurationStore.JackettFile, JackettOptions.SectionName);

        Assert.Equal("http://jackett:9117/", loaded.BaseUrl);
        Assert.Equal(["[SubsPlease]", "[Erai-raws]"], loaded.AnimeAuthors);
        Assert.Equal("knaben", loaded.Indexer["Shows"]);
        Assert.False(File.Exists(Path.Combine(_configDir, PirateFinConfigurationStore.JackettFile + ".tmp")));
    }

    [Fact]
    public async Task SaveJackett_RoundTripsTheDubAndSubGroups()
    {
        var options = new JackettOptions
        {
            AnimeDubGroups = [new() { Name = "Yameii", Indicator = "English Dub" }],
            AnimeSubGroups = [new() { Name = "ToonsHub", Indicator = "Multi-Subs" }, new() { Name = "[SubsPlease]" }],
            PrioritizeDub = true
        };

        await _store.SaveJackettAsync(options, CancellationToken.None);
        var loaded = Load<JackettOptions>(PirateFinConfigurationStore.JackettFile, JackettOptions.SectionName);

        Assert.Equal("Yameii", loaded.AnimeDubGroups.Single().Name);
        Assert.Equal("English Dub", loaded.AnimeDubGroups.Single().Indicator);
        Assert.Equal(["ToonsHub", "[SubsPlease]"], loaded.AnimeSubGroups.Select(g => g.Name));
        Assert.Equal("Multi-Subs", loaded.AnimeSubGroups[0].Indicator);
        Assert.True(loaded.PrioritizeDub);
    }

    [Fact]
    public async Task SaveProviders_RepeatedSaves_DoNotDuplicateArrays()
    {
        var options = new InternetProviderOptions { AllowedLanguages = ["en"] };

        for (var i = 0; i < 3; i++)
        {
            await _store.SaveProvidersAsync(options, CancellationToken.None);
            options = Load<InternetProviderOptions>(PirateFinConfigurationStore.ProvidersFile, InternetProviderOptions.SectionName);
        }

        Assert.Equal(["en"], options.AllowedLanguages);
        Assert.Null(options.ExcludedAnimeCountries);
    }

    [Fact]
    public async Task SaveEmailPublicAddress_KeepsTheAccount()
    {
        var current = new GmailOptions { Address = "piratefin@gmail.com", FromName = "Home", AppPassword = "secret" };
        var store = new PirateFinConfigurationStore(
            Mock.Of<IApplicationPaths>(p => p.ConfigurationDirectoryPath == _configDir),
            Mock.Of<IOptionsMonitor<InternetProviderOptions>>(),
            Mock.Of<IOptionsMonitor<JackettOptions>>(),
            Mock.Of<IOptionsMonitor<QBitTorrentOptions>>(),
            Mock.Of<IOptionsMonitor<GmailOptions>>(m => m.CurrentValue == current),
            Mock.Of<IOptionsMonitor<TrailerOptions>>(),
            Mock.Of<IOptionsMonitor<YouTubeOptions>>(),
            NullLogger<PirateFinConfigurationStore>.Instance);

        await store.SaveEmailPublicAddressAsync(string.Empty, CancellationToken.None);
        var loaded = Load<GmailOptions>(PirateFinConfigurationStore.GmailFile, GmailOptions.SectionName);

        Assert.Equal(string.Empty, loaded.PublicAddress);
        Assert.Equal("piratefin@gmail.com", loaded.Address);
        Assert.Equal("Home", loaded.FromName);
        Assert.Equal("secret", loaded.AppPassword);
    }

    [Fact]
    public async Task SaveGmail_EncryptsTheAppPassword()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await _store.SaveGmailAsync("piratefin@gmail.com", "PirateFin", "abcdefghijklmnop", CancellationToken.None);
        var loaded = Load<GmailOptions>(PirateFinConfigurationStore.GmailFile, GmailOptions.SectionName);

        Assert.Equal("piratefin@gmail.com", loaded.Address);
        Assert.DoesNotContain("abcdefghijklmnop", await File.ReadAllTextAsync(Path.Combine(_configDir, PirateFinConfigurationStore.GmailFile), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(GmailAppPasswordProtector.TryUnprotect(loaded.AppPassword, out var appPassword));
        Assert.Equal("abcdefghijklmnop", appPassword);
    }
}
