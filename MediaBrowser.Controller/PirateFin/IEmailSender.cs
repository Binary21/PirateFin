using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.PirateFin;

/// <summary>
/// Sends PirateFin's emails from the Gmail account configured in the dashboard.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Gets a value indicating whether a Gmail address and app password are configured.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends an email (HTML, with its plain text alternative and images).
    /// </summary>
    /// <param name="to">The recipient's address.</param>
    /// <param name="email">The email.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="System.InvalidOperationException">Email isn't configured.</exception>
    /// <exception cref="System.Net.Mail.SmtpException">The server refused the email.</exception>
    Task SendAsync(string to, PirateFinEmail email, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the dashboard's test email.
    /// </summary>
    /// <param name="to">The recipient's address.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="System.InvalidOperationException">Email isn't configured.</exception>
    /// <exception cref="System.Net.Mail.SmtpException">The server refused the email.</exception>
    Task SendTestAsync(string to, CancellationToken cancellationToken);
}
