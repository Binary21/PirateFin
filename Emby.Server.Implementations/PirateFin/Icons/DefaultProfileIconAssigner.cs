using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Icons;

/// <summary>
/// Gives every user without a profile image a random default icon: existing users when the server starts, new
/// users as they are created. Once given, the icon is kept until the user picks another one.
/// </summary>
public sealed class DefaultProfileIconAssigner : IHostedService, IEventConsumer<UserCreatedEventArgs>
{
    private readonly IUserManager _userManager;
    private readonly IIconPackService _iconPackService;
    private readonly ILogger<DefaultProfileIconAssigner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultProfileIconAssigner"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="iconPackService">The icon pack service.</param>
    /// <param name="logger">The logger.</param>
    public DefaultProfileIconAssigner(IUserManager userManager, IIconPackService iconPackService, ILogger<DefaultProfileIconAssigner> logger)
    {
        _userManager = userManager;
        _iconPackService = iconPackService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _iconPackService.InstallBundledPacks();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to install the bundled profile icon packs");
        }

        foreach (var user in _userManager.GetUsers())
        {
            await AssignAsync(user.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs) => AssignAsync(eventArgs.Argument.Id, CancellationToken.None);

    private async Task AssignAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            // Fetched again so the tracked entity is updated.
            if (_userManager.GetUserById(userId) is { } user)
            {
                await _iconPackService.AssignDefaultIconIfMissingAsync(user, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to give user {UserId} a default profile icon", userId);
        }
    }
}
