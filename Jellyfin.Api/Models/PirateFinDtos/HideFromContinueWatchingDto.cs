using System;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A title to remove from Continue Watching.
/// </summary>
public class HideFromContinueWatchingDto
{
    /// <summary>
    /// Gets or sets the movie, series or episode id.
    /// </summary>
    public Guid ItemId { get; set; }
}
