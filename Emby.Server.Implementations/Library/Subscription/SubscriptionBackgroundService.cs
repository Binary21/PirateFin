using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class SubscriptionBackgroundService : BackgroundService
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<SubscriptionBackgroundService> _logger;

    public SubscriptionBackgroundService(
        ISubscriptionService subscriptionService,
        ILogger<SubscriptionBackgroundService> logger)
    {
        _subscriptionService = subscriptionService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Subscription background service started...");
        try
        {
            await _subscriptionService.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Subscription background service stopping due to cancellation.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription background service crashed.");
        }
    }
}
