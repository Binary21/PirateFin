using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Subscriptions;

/// <summary>
/// Runs <see cref="ISubscriptionService.ProcessDueEpisodesAsync"/> on a fixed interval.
/// </summary>
public sealed class SubscriptionBackgroundService : BackgroundService
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IOptionsMonitor<SubscriptionOptions> _options;
    private readonly ILogger<SubscriptionBackgroundService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionBackgroundService"/> class.
    /// </summary>
    /// <param name="subscriptionService">The subscription service.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    public SubscriptionBackgroundService(ISubscriptionService subscriptionService, IOptionsMonitor<SubscriptionOptions> options, ILogger<SubscriptionBackgroundService> logger)
    {
        _subscriptionService = subscriptionService;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CurrentValue.ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _subscriptionService.ProcessDueEpisodesAsync(stoppingToken).ConfigureAwait(false);
                await _subscriptionService.ProcessVersionSearchesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad pass must not stop the loop for good.
                _logger.LogError(ex, "Source search pass failed");
            }
        }
    }
}
