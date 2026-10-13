using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class SubscriptionServiceCollectionExtensions
{
    public static IServiceCollection AddAnimeSubscriptionServices(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SubscriptionOptions>(config.GetSection("AnimeSubscriptions"));
        services.Configure<QBitTorrentOptions>(config.GetSection("QBitTorrent"));
        services.Configure<JackettOptions>(config.GetSection("Jackett"));

        services.AddSingleton<IEpisodeSubscriptionRepository, EpisodeSubscriptionRepository>();
        services.AddSingleton<ISubscriptionService, SubscriptionService>();
        services.AddSingleton<JackettSearchService>();

        services.AddSingleton<QBitTorrentManager>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<QBitTorrentOptions>>().Value;
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<QBitTorrentManager>>();

            return new QBitTorrentManager(
                options.Url,
                options.Username,
                options.Password,
                options.ExecutablePath,
                logger);
        });

        services.AddSingleton<IDownloadUrlService, DownloadUrlService>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<IDownloadManager>(sp => sp.GetRequiredService<DownloadManager>());
        services.AddHostedService(sp => sp.GetRequiredService<DownloadManager>());

        services.AddHostedService<SubscriptionBackgroundService>();

        return services;
    }
}
