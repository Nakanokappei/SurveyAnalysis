namespace SurveyAnalysis.Data;

// Transparent field-level protection for PII stored in the database. Encode is applied only when writing a
// PII field's value; Decode is applied to every value read back — it passes non-encrypted plaintext through
// unchanged — so upper layers always see plaintext and the storage layer alone decides what is encrypted.
// When the data key cannot be unwrapped (the database was moved to another Windows user / machine), Decode
// returns the Mask marker instead of throwing, so PII simply shows as 🔒.
public interface IDataProtector
{
    // True when the data key is available (PII can be read and written). False on a foreign user / machine
    // where the DPAPI-wrapped key cannot be unwrapped.
    bool IsUnlocked { get; }

    // The marker Decode returns when an encrypted value cannot be recovered (locked key / tampering).
    string Mask { get; }

    // Encrypts a value for storage. Called only for PII fields. Empty stays empty.
    string Encode(string plaintext);

    // Recovers a stored value: our ciphertext is decrypted, plaintext passes through unchanged, and an
    // unrecoverable ciphertext yields Mask.
    string Decode(string stored);

    // True when a stored value is already one of ours (encrypted), so a migration can skip re-encoding it.
    bool IsEncoded(string stored);
}

// The no-op protector: stores and returns values verbatim (no encryption). The default for repositories
// and tests, so behaviour is unchanged wherever a real protector is not wired.
public sealed class IdentityDataProtector : IDataProtector
{
    public static readonly IdentityDataProtector Instance = new();

    public bool IsUnlocked => true;
    public string Mask => "";
    public string Encode(string plaintext) => plaintext;
    public string Decode(string stored) => stored;
    public bool IsEncoded(string stored) => false;
}
