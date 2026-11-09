using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.PirateFin.Models;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Creates placeholder movies.
/// </summary>
public interface IMovieGenerator
{
    /// <summary>
    /// Creates or updates placeholders for the given movies.
    /// </summary>
    /// <param name="scheduleMovies">The movies.</param>
    /// <param name="library">The library to create the items in.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task GenerateMoviesAsync(IReadOnlyList<ScheduleMovie> scheduleMovies, string library, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the id of the movie's placeholder (or downloaded movie) if it exists in the library.
    /// </summary>
    /// <param name="scheduleMovie">The movie.</param>
    /// <param name="library">The library.</param>
    /// <returns>The id, or <c>null</c> when the movie does not exist.</returns>
    System.Guid? FindExistingMovieId(ScheduleMovie scheduleMovie, string library);
}
