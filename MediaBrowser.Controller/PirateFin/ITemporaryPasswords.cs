using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Emailed temporary passwords for users who forgot theirs. A temporary password works on the normal sign in screen
/// for 30 minutes and only replaces the user's password when it's used, so a stranger asking for one can't lock the
/// user out. After signing in with one, the user has to choose a new password.
/// </summary>
public interface ITemporaryPasswords
{
    /// <summary>
    /// Emails a new temporary password to the user, when they have an email address and email is set up.
    /// </summary>
    /// <param name="username">The user name entered on the forgot password page.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if an email was sent (or one was sent moments ago); <c>false</c> when there's no such user,
    /// they have no email address or email isn't set up.</returns>
    Task<bool> SendAsync(string username, CancellationToken cancellationToken);

    /// <summary>
    /// Checks a password entered at sign in against the user's temporary password and, when it matches, uses it up and
    /// marks that the user must choose a new password.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="password">The password entered.</param>
    /// <returns><c>true</c> if it's the user's (unexpired) temporary password.</returns>
    bool TryRedeem(Guid userId, string password);

    /// <summary>
    /// Gets a value indicating whether the user signed in with a temporary password and hasn't chosen a new one yet.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns><c>true</c> if they must choose a new password.</returns>
    bool IsChangeRequired(Guid userId);

    /// <summary>
    /// Records that the user chose a new password.
    /// </summary>
    /// <param name="userId">The user id.</param>
    void ClearChangeRequired(Guid userId);
}
