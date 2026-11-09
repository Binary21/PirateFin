using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// The emails' posters and links. Posters come from Jellyfin's image processor (resized and cached like the app's own posters).
/// </summary>
public sealed class EmailAssets : IEmailAssets
{
    // Twice the 170 px the poster is shown at, so it's sharp on phones.
    private const int PosterWidth = 340;

    private readonly IImageProcessor _imageProcessor;
    private readonly IOptionsMonitor<GmailOptions> _options;
    private readonly ILogger<EmailAssets> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailAssets"/> class.
    /// </summary>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="options">The email options (for the public address).</param>
    /// <param name="logger">The logger.</param>
    public EmailAssets(IImageProcessor imageProcessor, IOptionsMonitor<GmailOptions> options, ILogger<EmailAssets> logger)
    {
        _imageProcessor = imageProcessor;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetPosterAsync(BaseItem? item)
    {
        var owner = item switch
        {
            Episode episode => (BaseItem?)episode.Series ?? episode,
            Season season => (BaseItem?)season.Series ?? season,
            _ => item
        };

        var image = owner?.GetImageInfo(ImageType.Primary, 0);
        if (owner is null || image is null)
        {
            return null;
        }

        try
        {
            var (path, _, _) = await _imageProcessor.ProcessImage(new ImageProcessingOptions
            {
                Item = owner,
                ItemId = owner.Id,
                Image = image,
                ImageIndex = 0,
                MaxWidth = PosterWidth,
                Quality = 85,
                SupportedOutputFormats = [ImageFormat.Jpg]
            }).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not load the poster of {Name} for an email", owner.Name);
            return null;
        }
    }

    /// <inheritdoc />
    public string? GetItemLink(Guid itemId)
        => GetLink("details?id=" + itemId.ToString("N", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public string? GetLink(string route)
    {
        var address = _options.CurrentValue.PublicAddress?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(address) ? null : $"{address}/web/#/{route}";
    }
}
