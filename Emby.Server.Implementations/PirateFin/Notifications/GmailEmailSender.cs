using System;
using System.IO;
using System.Net.Mail;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Security;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Options;
using MimeKit;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// Sends email from the Gmail account in gmail.json through smtp.gmail.com (port 587, STARTTLS), signing in with the
/// account's app password. Uses MailKit: System.Net.Mail's SmtpClient ignores Gmail's "535 5.7.8 Username and Password
/// not accepted" and only reports the connection Gmail closes afterwards.
/// </summary>
public sealed class GmailEmailSender : IEmailSender
{
    private const string SmtpHost = "smtp.gmail.com";
    private const int SmtpPort = 587;

    private readonly IOptionsMonitor<GmailOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="GmailEmailSender"/> class.
    /// </summary>
    /// <param name="options">The Gmail options.</param>
    public GmailEmailSender(IOptionsMonitor<GmailOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public bool IsConfigured
        => !string.IsNullOrWhiteSpace(_options.CurrentValue.Address) && !string.IsNullOrEmpty(_options.CurrentValue.AppPassword);

    /// <inheritdoc />
    public async Task SendAsync(string to, PirateFinEmail email, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Email isn't set up: enter the Gmail address and app password in the dashboard.");
        }

        if (!GmailAppPasswordProtector.TryUnprotect(options.AppPassword, out var appPassword))
        {
            throw new InvalidOperationException(
                "The saved app password can't be read (it was saved by another Windows account or machine). Enter it again in the dashboard.");
        }

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName, options.Address));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = email.Subject;

        // multipart/alternative (text + HTML), the HTML's images attached inline and referred to by Content-Id.
        var body = new BodyBuilder { TextBody = email.Text, HtmlBody = email.Html };
        foreach (var image in email.Images)
        {
            body.LinkedResources.Add(new MimePart(ContentType.Parse(image.MimeType))
            {
                Content = new MimeContent(new MemoryStream(image.Data.ToArray())),
                ContentId = image.ContentId,
                ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                ContentTransferEncoding = ContentEncoding.Base64
            });
        }

        message.Body = body.ToMessageBody();

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(SmtpHost, SmtpPort, SecureSocketOptions.StartTls, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(options.Address, appPassword, cancellationToken).ConfigureAwait(false);
            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSignInRejected(ex))
        {
            throw new SmtpException(
                $"Gmail didn't accept the sign in for {options.Address}. Check the address and that you entered an app password "
                + "created for that account (2-Step Verification must be on), not its normal password. Gmail said: " + ex.Message,
                ex);
        }
        catch (Exception ex) when (ex is CommandException or ProtocolException or IOException or SocketException or SslHandshakeException)
        {
            throw new SmtpException("Gmail couldn't send the email: " + ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public Task SendTestAsync(string to, CancellationToken cancellationToken)
        => SendAsync(
            to,
            new EmailBuilder("PirateFin test email", "Email works.")
                .Label("Test email")
                .Title("Email works")
                .Text("Users who enter their address on their profile page will get an email when a new episode of a season they follow is ready.")
                .Build(),
            cancellationToken);

    // Gmail answers 535 5.7.8 to a wrong address / app password, and sometimes disconnects right after it.
    private static bool IsSignInRejected(Exception ex)
        => ex is AuthenticationException
            || (ex is CommandException or ProtocolException
                && (ex.Message.Contains("5.7.8", StringComparison.Ordinal) || ex.Message.Contains("BadCredentials", StringComparison.Ordinal)));
}
