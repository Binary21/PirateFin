using System;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The library series created for a search result.
/// </summary>
public class OpenedSeriesDto
{
    /// <summary>
    /// Gets or sets the series id.
    /// </summary>
    public Guid ItemId { get; set; }
}
