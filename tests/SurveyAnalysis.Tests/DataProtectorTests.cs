using System;
using System.Security.Cryptography;
using SurveyAnalysis.Data;
using Xunit;

namespace SurveyAnalysis.Tests;

// IDataProtector protects PII at rest. DpapiDataProtector encrypts with a per-DB AES key (round-trips, a
// fresh nonce each time, fails closed to a mask on the wrong/locked key) and passes legacy / non-PII
// plaintext through unchanged. DataKeyStore persists the key (DPAPI-wrapped) so a reopened database
// recovers the same protector.
public class DataProtectorTests
{
    private static DpapiDataProtector WithRandomKey() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Encrypts_and_recovers_a_value()
    {
        var p = WithRandomKey();
        var cipher = p.Encode("山田 太郎");
        Assert.StartsWith("enc1:", cipher);
        Assert.DoesNotContain("山田", cipher);          // not stored in plaintext
        Assert.Equal("山田 太郎", p.Decode(cipher));
    }

    [Fact]
    public void Passes_legacy_and_non_pii_plaintext_through()
    {
        var p = WithRandomKey();
        Assert.Equal("東京都新宿区1-1", p.Decode("東京都新宿区1-1"));   // never encrypted → unchanged
        Assert.Equal("", p.Decode(""));
        Assert.Equal("", p.Encode(""));                                // empty stays empty
    }

    [Fact]
    public void Each_encryption_is_unique_but_decrypts_the_same()
    {
        var p = WithRandomKey();
        var a = p.Encode("03-1234-5678");
        var b = p.Encode("03-1234-5678");
        Assert.NotEqual(a, b);                 // random nonce per call
        Assert.Equal("03-1234-5678", p.Decode(a));
        Assert.Equal("03-1234-5678", p.Decode(b));
    }

    [Fact]
    public void A_wrong_key_yields_the_mask_not_a_throw()
    {
        var cipher = WithRandomKey().Encode("090-0000-0000");
        var other = WithRandomKey();           // a different key
        Assert.Equal("🔒", other.Decode(cipher));
    }

    [Fact]
    public void A_locked_protector_masks_reads_and_refuses_to_encrypt()
    {
        var cipher = WithRandomKey().Encode("secret@example.com");
        var locked = new DpapiDataProtector(null);
        Assert.False(locked.IsUnlocked);
        Assert.Equal("🔒", locked.Decode(cipher));
        Assert.Equal("plain", locked.Decode("plain"));   // plaintext still passes through
        Assert.Throws<InvalidOperationException>(() => locked.Encode("x"));
    }

    [Fact]
    public void Identity_protector_is_a_no_op()
    {
        var p = IdentityDataProtector.Instance;
        Assert.Equal("山田", p.Encode("山田"));
        Assert.Equal("山田", p.Decode("山田"));
        Assert.True(p.IsUnlocked);
    }

    [Fact]
    public void Key_store_persists_the_key_so_a_reopened_database_recovers_it()
    {
        using var temp = new TempDatabase();
        var first = DataKeyStore.Load(temp.Db);
        Assert.True(first.IsUnlocked);
        var cipher = first.Encode("山田 太郎");

        // A fresh Load over the same database file must produce a protector with the same key.
        var second = DataKeyStore.Load(temp.Db);
        Assert.Equal("山田 太郎", second.Decode(cipher));
    }
}
