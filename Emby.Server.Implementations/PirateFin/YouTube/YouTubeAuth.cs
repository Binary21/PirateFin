namespace Emby.Server.Implementations.PirateFin.YouTube;

/// <summary>
/// How a YouTube Data API call signs in: a user's OAuth access token, or the server's API key (public data only).
/// </summary>
/// <param name="AccessToken">The OAuth access token, or <c>null</c>.</param>
/// <param name="ApiKey">The API key, or <c>null</c>.</param>
public sealed record YouTubeAuth(string? AccessToken, string? ApiKey);
