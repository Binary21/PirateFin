using System.Net;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Configuration;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// PirateFin: administrators signed in from outside the home network are treated as ordinary users, unless
/// <see cref="ServerConfiguration.AllowRemoteAdminAccess"/> is on.
/// </summary>
public static class RemoteAdminAccess
{
    /// <summary>
    /// Gets a value indicating whether a request from this address loses its admin rights.
    /// </summary>
    /// <param name="networkManager">The network manager (what counts as the home network).</param>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="remoteIp">The request's address; <c>null</c> when unknown (e.g. internal calls).</param>
    /// <returns><c>true</c> when admin rights are withheld.</returns>
    public static bool IsBlocked(INetworkManager networkManager, ServerConfiguration configuration, IPAddress? remoteIp)
        => !configuration.AllowRemoteAdminAccess && remoteIp is not null && !networkManager.IsInLocalNetwork(remoteIp);
}
