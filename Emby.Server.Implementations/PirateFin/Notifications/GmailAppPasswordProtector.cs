using System;
using System.Security.Cryptography;
using System.Text;

namespace Emby.Server.Implementations.PirateFin.Notifications;

/// <summary>
/// Encrypts the Gmail app password in gmail.json with Windows DPAPI for the account the server runs as, so a copy of
/// the file is useless on another machine or account.
/// </summary>
public static class GmailAppPasswordProtector
{
    private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("PirateFin.Gmail.AppPassword");

    /// <summary>
    /// Encrypts an app password.
    /// </summary>
    /// <param name="appPassword">The app password.</param>
    /// <returns>The encrypted password, base64 encoded.</returns>
    /// <exception cref="PlatformNotSupportedException">The server isn't running on Windows.</exception>
    public static string Protect(string appPassword)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Gmail app password can only be encrypted on Windows.");
        }

        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(appPassword), _entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Decrypts an app password saved by <see cref="Protect"/>.
    /// </summary>
    /// <param name="protectedPassword">The encrypted password, base64 encoded.</param>
    /// <param name="appPassword">The app password, when it could be decrypted.</param>
    /// <returns><c>true</c> if it could be decrypted; <c>false</c> if it's empty, damaged or was encrypted by another
    /// Windows account or machine.</returns>
    public static bool TryUnprotect(string protectedPassword, out string appPassword)
    {
        appPassword = string.Empty;
        if (string.IsNullOrEmpty(protectedPassword) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), _entropy, DataProtectionScope.CurrentUser);
            appPassword = Encoding.UTF8.GetString(decrypted);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
