using System;
using System.Security.Cryptography;
using System.Text;

namespace SurveyAnalysis.Data;

// Protects the three secret settings (Gmail app password, SMTP password, OpenAI API key) before
// they are written to the database. On Windows — the production target — values are encrypted with
// DPAPI (CurrentUser scope), so the database alone cannot reveal them. On macOS/Linux (development
// preview only) DPAPI is unavailable, so the value is stored as-is. A scheme prefix records which
// path produced the stored string, so reading is unambiguous regardless of where it was written.
public static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    // Returns a self-describing string safe to persist for the given plaintext secret.
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return PlainPrefix; // empty stays empty (no secret set)

        if (OperatingSystem.IsWindows())
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(cipher);
        }

        return PlainPrefix + plaintext;
    }

    // Recovers the plaintext from a previously protected string. A value encrypted with DPAPI on
    // Windows cannot be read on macOS; in that case (or any decrypt failure) it returns empty so the
    // field simply shows blank rather than throwing.
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";

        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return stored[PlainPrefix.Length..];

        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                return ""; // DPAPI ciphertext is not portable off Windows

            try
            {
                var cipher = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                var plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException)
            {
                return "";
            }
        }

        // Unprefixed legacy value: treat as plaintext.
        return stored;
    }
}
