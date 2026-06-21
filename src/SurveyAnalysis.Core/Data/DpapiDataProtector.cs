using System;
using System.Security.Cryptography;
using System.Text;

namespace SurveyAnalysis.Data;

// Envelope encryption for PII: a per-database AES-256 key (the "private key") encrypts the values with
// AES-GCM, while the key itself is wrapped at rest by DPAPI (the "master key", CurrentUser) — see
// DataKeyStore. Ciphertext is self-describing ("enc1:" + base64(nonce|ciphertext|tag)) so Decode can tell
// an encrypted value from legacy plaintext and pass the latter through unchanged. A locked protector (no
// key, e.g. the DB opened by another Windows user) leaves reads masked and refuses to encrypt.
public sealed class DpapiDataProtector : IDataProtector
{
    private const string Prefix = "enc1:";
    private const int NonceSize = 12;   // AES-GCM standard nonce length
    private const int TagSize = 16;     // AES-GCM authentication tag length

    private readonly byte[]? _key;   // null = locked (the key could not be unwrapped on this user/machine)

    public DpapiDataProtector(byte[]? key) => _key = key;

    public bool IsUnlocked => _key is not null;
    public string Mask => "🔒";

    public string Encode(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;
        if (_key is null)
            throw new InvalidOperationException("The PII data key is unavailable on this user/machine; cannot encrypt.");

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(_key, TagSize))
            gcm.Encrypt(nonce, plain, cipher, tag);

        // Lay out nonce | ciphertext | tag so Decode can split it back without separate length fields.
        var blob = new byte[NonceSize + cipher.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize, cipher.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceSize + cipher.Length, TagSize);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string Decode(string stored)
    {
        // Empty stays empty; anything without our prefix is legacy / non-PII plaintext and passes through.
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        if (_key is null)
            return Mask;   // an encrypted value, but the key is unavailable here

        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            if (blob.Length < NonceSize + TagSize)
                return Mask;
            var nonce = blob[..NonceSize];
            var tag = blob[^TagSize..];
            var cipher = blob[NonceSize..^TagSize];
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(_key, TagSize))
                gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Wrong key (foreign DB) or tampered ciphertext — fail closed to the mask, never throw.
            return Mask;
        }
    }
}
