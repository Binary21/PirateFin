using System;
using System.Security.Cryptography;
using System.Text;

namespace Emby.Server.Implementations.PirateFin.Security;

/// <summary>
/// Encrypts PirateFin's secrets (OAuth client secrets, sign in tokens) with Windows DPAPI for the account the server
/// runs as, so a copy of the files is useless on another machine or account. Each kind of secret uses its own purpose.
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// Encrypts a secret.
    /// </summary>
    /// <param name="secret">The secret.</param>
    /// <param name="purpose">What it is, e.g. "YouTube.ClientSecret".</param>
    /// <returns>The encrypted secret, base64 encoded.</returns>
    /// <exception cref="PlatformNotSupportedException">The server isn't running on Windows.</exception>
    public static string Protect(string secret, string purpose)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secrets can only be encrypted on Windows.");
        }

        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy(purpose), DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// Decrypts a secret saved by <see cref="Protect"/>.
    /// </summary>
    /// <param name="protectedSecret">The encrypted secret.</param>
    /// <param name="purpose">What it is.</param>
    /// <param name="secret">The secret, when it could be decrypted.</param>
    /// <returns><c>true</c> if it could be decrypted.</returns>
    public static bool TryUnprotect(string? protectedSecret, string purpose, out string secret)
    {
        secret = string.Empty;
        if (string.IsNullOrEmpty(protectedSecret) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            secret = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy(purpose), DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("PirateFin." + purpose);
}
