using System;
using Emby.Server.Implementations.PirateFin.Downloads;
using Emby.Server.Implementations.PirateFin.Icons;
using Emby.Server.Implementations.PirateFin.Insights;
using Emby.Server.Implementations.PirateFin.Library;
using Emby.Server.Implementations.PirateFin.Notifications;
using Emby.Server.Implementations.PirateFin.Profiles;
using Emby.Server.Implementations.PirateFin.Providers;
using Emby.Server.Implementations.PirateFin.Requests;
using Emby.Server.Implementations.PirateFin.Storage;
using Emby.Server.Implementations.PirateFin.Subscriptions;
using Emby.Server.Implementations.PirateFin.Torrents;
using Emby.Server.Implementations.PirateFin.Trailers;
using Emby.Server.Implementations.PirateFin.YouTube;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin;

/// <summary>
/// Registers every PirateFin service.
/// </summary>
public static class PirateFinServiceCollectionExtensions
{
    /// <summary>
    /// Adds the PirateFin services and binds providers.json / jackett.json / qbittorrent.json.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddPirateFin(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InternetProviderOptions>(configuration.GetSection(InternetProviderOptions.SectionName));
        services.Configure<JackettOptions>(configuration.GetSection(JackettOptions.SectionName));
        services.PostConfigure<JackettOptions>(o => o.MigrateAnimeAuthors());
        services.Configure<QBitTorrentOptions>(configuration.GetSection(QBitTorrentOptions.SectionName));
        services.Configure<SubscriptionOptions>(configuration.GetSection(SubscriptionOptions.SectionName));
        services.Configure<GmailOptions>(configuration.GetSection(GmailOptions.SectionName));
        services.Configure<TrailerOptions>(configuration.GetSection(TrailerOptions.SectionName));
        services.Configure<YouTubeOptions>(configuration.GetSection(YouTubeOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        // Providers
        services.AddSingleton<TraktClient>();
        services.AddSingleton<AniListClient>();
        services.AddSingleton<AnimeEpisodeDetails>();
        services.AddSingleton<YtsClient>();
        services.AddSingleton<IInternetScheduleFetcher, InternetScheduleFetcher>();
        services.AddSingleton<IInternetSearchService, InternetSearchService>();

        // Popular rows
        services.AddSingleton<TmdbPopularClient>();
        services.AddSingleton<PopularService>();
        services.AddSingleton<IPopularTitles>(sp => sp.GetRequiredService<PopularService>());
        services.AddHostedService(sp => sp.GetRequiredService<PopularService>());

        // Library
        services.AddSingleton<MetadataRefreshQueue>();
        services.AddSingleton<IMetadataRefreshQueue>(sp => sp.GetRequiredService<MetadataRefreshQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<MetadataRefreshQueue>());
        services.AddSingleton<SeriesPlaceholderWriter>();
        services.AddSingleton<IEpisodeGenerator, EpisodeGenerator>();
        services.AddSingleton<ISeriesGenerator, SeriesGenerator>();
        services.AddSingleton<IMovieGenerator, MovieGenerator>();
        services.AddSingleton<IPirateFinLibraryService, PirateFinLibraryService>();
        services.AddSingleton<IPirateFinConfigurationStore, PirateFinConfigurationStore>();
        services.AddHostedService<EmptySeasonCleanup>();
        services.AddHostedService<DuplicateSeasonMerger>();

        // Filling in the Shows / Anime series: nightly, and a full run from the dashboard
        services.AddSingleton<LibraryPopulator>();
        services.AddSingleton<ILibraryPopulator>(sp => sp.GetRequiredService<LibraryPopulator>());
        services.AddHostedService(sp => sp.GetRequiredService<LibraryPopulator>());

        // Storage: which drive downloads go to
        services.AddSingleton<LibraryStorage>();
        services.AddSingleton<ILibraryStorage>(sp => sp.GetRequiredService<LibraryStorage>());

        // Torrents / downloads
        services.AddSingleton<JackettClient>();
        services.AddSingleton<ITorrentSearchService, TorrentSearchService>();
        services.AddSingleton<QBitTorrentClient>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<IDownloadManager>(sp => sp.GetRequiredService<DownloadManager>());
        services.AddHostedService(sp => sp.GetRequiredService<DownloadManager>());
        services.AddSingleton<PlayableTimeEstimator>();
        services.AddSingleton<IDownloadProgress, DownloadProgressService>();
        services.AddSingleton<IWebSocketListener, DownloadProgressWebSocketListener>();
        services.AddHostedService<NextEpisodePrefetcher>();

        // Overnight: series split across drives are moved back onto one
        services.AddSingleton<StorageRebalancer>();
        services.AddSingleton<IStorageRebalancer>(sp => sp.GetRequiredService<StorageRebalancer>());
        services.AddHostedService(sp => sp.GetRequiredService<StorageRebalancer>());

        // Profile icons
        services.AddSingleton<IIconPackService, IconPackService>();
        services.AddHostedService<DefaultProfileIconAssigner>();
        services.AddScoped<IEventConsumer<UserCreatedEventArgs>, DefaultProfileIconAssigner>();

        // Email
        services.AddSingleton<IEmailSender, GmailEmailSender>();
        services.AddSingleton<IEmailAssets, EmailAssets>();
        services.AddSingleton<EpisodeEmailBatches>();
        services.AddSingleton<SlowDownloadNotifier>();
        services.AddSingleton<ISlowDownloads>(sp => sp.GetRequiredService<SlowDownloadNotifier>());
        services.AddHostedService(sp => sp.GetRequiredService<SlowDownloadNotifier>());
        services.AddSingleton<IUserNotificationSettings, UserNotificationSettingsStore>();
        services.AddSingleton<IUserInbox, UserInboxStore>();
        services.AddSingleton<ITemporaryPasswords, TemporaryPasswordService>();

        // Profiles: PIN, kids, removed from Continue Watching
        services.AddSingleton<ProfileSettingsStore>();
        services.AddSingleton<IProfileSettings>(sp => sp.GetRequiredService<ProfileSettingsStore>());
        services.AddHostedService(sp => sp.GetRequiredService<ProfileSettingsStore>());
        services.AddHostedService<NewEpisodeNotifier>();

        // Download requests to admins
        services.AddSingleton<IDownloadRequests, DownloadRequestService>();

        // Subscriptions
        services.AddSingleton<SeasonSubscriptionStore>();
        services.AddSingleton<ISeasonSubscriptions>(sp => sp.GetRequiredService<SeasonSubscriptionStore>());
        services.AddHostedService(sp => sp.GetRequiredService<SeasonSubscriptionStore>());
        services.AddSingleton<IEpisodeSubscriptionRepository, EpisodeSubscriptionRepository>();
        services.AddSingleton<EpisodeVersions>();
        services.AddSingleton<IEpisodeVersions>(sp => sp.GetRequiredService<EpisodeVersions>());
        services.AddHostedService(sp => sp.GetRequiredService<EpisodeVersions>());
        services.AddSingleton<ISubscriptionService, SubscriptionService>();
        services.AddSingleton<IReleaseSearchPauses, ReleaseSearchPauseStore>();
        services.AddSingleton<IReleaseSearchStatus, ReleaseSearchStatusService>();
        services.AddHostedService<SubscriptionBackgroundService>();

        // Viewing history and genre rankings
        services.AddSingleton<IPlaybackHistory>(sp => new PlaybackHistoryStore(
            sp.GetRequiredService<IApplicationPaths>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PlaybackHistoryStore>>()));
        services.AddHostedService<PlaybackTracker>();
        services.AddSingleton<IGenreRanking, GenreRankingService>();
        services.AddSingleton<IUserInsights, UserInsightsService>();

        // Trailers saved next to downloaded movies and series
        services.AddSingleton<YtDlp>();
        services.AddSingleton<TrailerLibrary>();
        services.AddSingleton<ITrailerLibrary>(sp => sp.GetRequiredService<TrailerLibrary>());
        services.AddHostedService(sp => sp.GetRequiredService<TrailerLibrary>());

        // The YouTube tab
        services.AddSingleton<YouTubeApi>();
        services.AddSingleton<YouTubeStreams>();
        services.AddSingleton<YouTubeService>();
        services.AddSingleton<IYouTube>(sp => sp.GetRequiredService<YouTubeService>());
        services.AddHostedService(sp => sp.GetRequiredService<YouTubeService>());

        return services;
    }
}
