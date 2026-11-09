using System;

namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// The episode or season to request.
/// </summary>
public class CreateRequestDto
{
    /// <summary>
    /// Gets or sets the episode or season id.
    /// </summary>
    public Guid ItemId { get; set; }
}
