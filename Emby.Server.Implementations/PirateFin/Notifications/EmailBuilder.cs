using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using MediaBrowser.Controller.PirateFin;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// Builds PirateFin's emails in the app's dark style: the red PIRATEFIN wordmark (its P is the logo), a dark card with
/// the poster on the left and the details on the right (stacked on phones), and a small footer. Email safe HTML: tables
/// and inline styles. A plain text version is built alongside.
/// </summary>
internal sealed class EmailBuilder
{
    /// <summary>PirateFin red.</summary>
    public const string Red = "#e50914";

    /// <summary>Good news (a request was found).</summary>
    public const string Green = "#46d369";

    /// <summary>Warnings (a slow download).</summary>
    public const string Yellow = "#f5c518";

    /// <summary>Secondary text.</summary>
    public const string Muted = "#a3a3a3";

    private const string Background = "#141414";
    private const string Card = "#1f1f1f";
    private const string Soft = "#d2d2d2";
    private const string Line = "#333333";
    private const string Font = "Helvetica, Arial, sans-serif";
    private const string LogoId = "piratefin-logo";
    private const string PosterId = "piratefin-poster";

    private static readonly Lazy<byte[]> _logo = new(() =>
    {
        using var stream = typeof(EmailBuilder).Assembly.GetManifestResourceStream("PirateFin.EmailLogo.png")
            ?? throw new InvalidOperationException("The email logo resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    private readonly string _subject;
    private readonly string _preheader;
    private readonly StringBuilder _intro = new();
    private readonly StringBuilder _details = new();
    private readonly StringBuilder _text = new();
    private readonly List<string> _footer = [];
    private EmailImage? _poster;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailBuilder"/> class.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="preheader">The line mail apps show after the subject in the inbox.</param>
    public EmailBuilder(string subject, string preheader)
    {
        _subject = subject;
        _preheader = preheader;
    }

    /// <summary>
    /// Adds "Hi {name}," above the card's content.
    /// </summary>
    /// <param name="name">The user name.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Greeting(string name)
    {
        _intro.Append(Paragraph($"Hi {E(name)},", "#ffffff", 15));
        TextLine($"Hi {name},").TextLine();
        return this;
    }

    /// <summary>
    /// Shows the poster to the left of the details.
    /// </summary>
    /// <param name="image">The poster (JPEG or PNG); nothing when <c>null</c>.</param>
    /// <param name="mimeType">The poster's type.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Poster(byte[]? image, string mimeType = "image/jpeg")
    {
        _poster = image is null ? null : new EmailImage(PosterId, image, mimeType);
        return this;
    }

    /// <summary>
    /// Adds the small upper case label, e.g. NEW EPISODE.
    /// </summary>
    /// <param name="text">The label.</param>
    /// <param name="color">Its colour.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Label(string text, string color = Red)
    {
        _details.Append(CultureInfo.InvariantCulture, $"""<div style="font-family:{Font};font-size:12px;font-weight:bold;letter-spacing:2px;color:{color};text-transform:uppercase;margin-bottom:8px;">{E(text)}</div>""");
        TextLine(text.ToUpperInvariant());
        return this;
    }

    /// <summary>
    /// Adds the big title, e.g. the series name.
    /// </summary>
    /// <param name="text">The title.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Title(string text)
    {
        _details.Append(CultureInfo.InvariantCulture, $"""<div style="font-family:{Font};font-size:24px;line-height:30px;font-weight:bold;color:#ffffff;margin-bottom:6px;">{E(text)}</div>""");
        TextLine(text);
        return this;
    }

    /// <summary>
    /// Adds the line under the title, e.g. "S10E02 · Fire in the Hole".
    /// </summary>
    /// <param name="text">The line.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Subtitle(string text)
    {
        _details.Append(CultureInfo.InvariantCulture, $"""<div style="font-family:{Font};font-size:17px;line-height:23px;font-weight:bold;color:#ffffff;margin-bottom:4px;">{E(text)}</div>""");
        TextLine(text);
        return this;
    }

    /// <summary>
    /// Adds a small grey line of details, e.g. "43 min · Aired Oct 22, 2026".
    /// </summary>
    /// <param name="text">The details.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Meta(string text)
    {
        _details.Append(CultureInfo.InvariantCulture, $"""<div style="font-family:{Font};font-size:13px;line-height:19px;color:{Muted};margin-bottom:12px;">{E(text)}</div>""");
        TextLine(text);
        return this;
    }

    /// <summary>
    /// Adds a paragraph.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="muted">Whether it is grey and smaller (a side note).</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Text(string text, bool muted = false)
    {
        _details.Append(muted ? Paragraph(E(text), Muted, 13) : Paragraph(E(text), Soft, 14));
        TextLine().TextLine(text);
        return this;
    }

    /// <summary>
    /// Adds a paragraph that starts with a grey caption, e.g. "Note from the admin: ...".
    /// </summary>
    /// <param name="caption">The caption.</param>
    /// <param name="text">The text.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Note(string caption, string text)
    {
        _details.Append(Paragraph($"""<span style="color:{Muted};">{E(caption)}</span> {E(text)}""", Soft, 14));
        TextLine().TextLine($"{caption} {text}");
        return this;
    }

    /// <summary>
    /// Adds a list of episodes: their number, then their title.
    /// </summary>
    /// <param name="rows">The episodes.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder EpisodeList(IEnumerable<(string Number, string Name)> rows)
    {
        _details.Append(CultureInfo.InvariantCulture, $"""<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="margin:4px 0;border-bottom:1px solid {Line};">""");
        TextLine();
        foreach (var (number, name) in rows)
        {
            _details.Append(CultureInfo.InvariantCulture, $"""<tr><td style="font-family:{Font};font-size:14px;color:{Muted};padding:9px 14px 9px 0;border-top:1px solid {Line};white-space:nowrap;width:1%;">{E(number)}</td><td style="font-family:{Font};font-size:14px;color:#ffffff;padding:9px 0;border-top:1px solid {Line};">{E(name)}</td></tr>""");
            TextLine(string.IsNullOrEmpty(name) ? $"    {number}" : $"    {number}  {name}");
        }

        _details.Append("</table>");
        return this;
    }

    /// <summary>
    /// Adds the temporary password in a big box.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder PasswordBox(string password)
    {
        _details.Append(CultureInfo.InvariantCulture, $"""<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:8px 0 18px 0;"><tr><td bgcolor="{Background}" style="background:{Background};border:1px solid {Line};border-radius:6px;padding:16px 24px;font-family:Consolas, 'Courier New', monospace;font-size:26px;letter-spacing:3px;color:#ffffff;">{E(password)}</td></tr></table>""");
        TextLine().TextLine("    " + password);
        return this;
    }

    /// <summary>
    /// Adds the red button; nothing when there is no link (no public address is set).
    /// </summary>
    /// <param name="label">The button's text.</param>
    /// <param name="url">Where it goes, or <c>null</c>.</param>
    /// <param name="play">Whether it starts with a play arrow.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Button(string label, string? url, bool play = false)
    {
        if (string.IsNullOrEmpty(url))
        {
            return this;
        }

        var arrow = play ? "&#9654;&nbsp; " : string.Empty;
        _details.Append(CultureInfo.InvariantCulture, $"""<table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin-top:20px;"><tr><td bgcolor="{Red}" style="border-radius:4px;"><a href="{E(url)}" style="display:inline-block;padding:12px 28px;font-family:{Font};font-size:16px;font-weight:bold;color:#ffffff;text-decoration:none;border-radius:4px;">{arrow}{E(label)}</a></td></tr></table>""");
        TextLine().TextLine($"{label}: {url}");
        return this;
    }

    /// <summary>
    /// Adds a line to the small grey footer under the card.
    /// </summary>
    /// <param name="text">The line.</param>
    /// <returns>This builder.</returns>
    public EmailBuilder Footer(string text)
    {
        _footer.Add(text);
        return this;
    }

    /// <summary>
    /// Builds the email.
    /// </summary>
    /// <returns>The email.</returns>
    public PirateFinEmail Build()
    {
        var footer = new List<string>(_footer) { "Sent by PirateFin." };
        var content = _poster is null
            ? _details.ToString()
            : $"""<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%"><tr><td class="pf-col pf-poster" valign="top" width="170" style="width:170px;padding:0 24px 0 0;"><img src="cid:{PosterId}" width="170" alt="" style="display:block;width:170px;height:auto;border-radius:6px;border:0;"></td><td class="pf-col" valign="top" style="padding:0;">{_details}</td></tr></table>""";

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="dark">
            <meta name="supported-color-schemes" content="dark">
            <title>{{E(_subject)}}</title>
            <style>
              @media (max-width: 520px) {
                .pf-col { display:block !important; width:100% !important; padding:0 !important; }
                .pf-poster { padding-bottom:20px !important; }
                .pf-poster img { width:150px !important; }
                .pf-pad { padding:24px 20px !important; }
              }
            </style>
            </head>
            <body style="margin:0;padding:0;background:{{Background}};">
            <div style="display:none;max-height:0;overflow:hidden;color:{{Background}};">{{E(_preheader)}}</div>
            <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" bgcolor="{{Background}}" style="background:{{Background}};">
              <tr><td align="center" style="padding:28px 12px;">
                <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="600" style="width:100%;max-width:600px;">
                  <tr><td style="padding:0 4px 20px 4px;font-family:'Arial Black', {{Font}};font-size:30px;font-weight:900;letter-spacing:1px;color:{{Red}};"><img src="cid:{{LogoId}}" height="22" alt="P" style="height:22px;width:auto;vertical-align:baseline;border:0;margin-right:2px;font-family:'Arial Black', {{Font}};font-size:30px;font-weight:900;color:{{Red}};">IRATEFIN</td></tr>
                  <tr><td class="pf-pad" bgcolor="{{Card}}" style="background:{{Card}};border-radius:8px;padding:32px;">{{_intro}}{{content}}</td></tr>
                  <tr><td style="padding:20px 4px 0 4px;font-family:{{Font}};font-size:12px;line-height:18px;color:#808080;">{{string.Join("<br>", footer.ConvertAll(E))}}</td></tr>
                </table>
              </td></tr>
            </table>
            </body>
            </html>
            """;

        var text = new StringBuilder(_text.ToString().Trim());
        text.AppendLine().AppendLine();
        foreach (var line in footer)
        {
            text.AppendLine(line);
        }

        var images = new List<EmailImage> { new(LogoId, _logo.Value, "image/png") };
        if (_poster is not null)
        {
            images.Add(_poster);
        }

        return new PirateFinEmail(_subject, html, text.ToString().TrimEnd(), images);
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string Paragraph(string html, string color, int size)
        => $"""<p style="font-family:{Font};font-size:{size}px;line-height:{size + 7}px;color:{color};margin:0 0 12px 0;">{html}</p>""";

    private EmailBuilder TextLine(string line = "")
    {
        _text.AppendLine(line);
        return this;
    }
}
