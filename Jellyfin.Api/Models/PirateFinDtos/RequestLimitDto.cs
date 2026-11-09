namespace Jellyfin.Api.Models.PirateFinDtos;

/// <summary>
/// A request limit: the most open requests at a time.
/// </summary>
public class RequestLimitDto
{
    /// <summary>
    /// Gets or sets the limit; -1 for no limit, <c>null</c> (for a user) to use the default.
    /// </summary>
    public int? Limit { get; set; }
}
