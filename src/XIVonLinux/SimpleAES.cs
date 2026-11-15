using System;
using System.Security.Cryptography;

namespace XIVonLinux;

/// <summary>Per-account AES-CBC (256-bit key, 128-bit IV) obfuscation of saved
/// passwords. NOT security hardening — it is obfuscation so the password is never
/// stored as plain text (Alex's rule). The key + vector are DERIVED from the
/// username: SHA-512 of the username gives 128 hex chars; the first 64 are the
/// AES-256 key, the last 32 the IV (Alex's rule 2026-11-14). Nothing crypto is
/// stored in accounts.deck beyond the ciphertext — the key material is recreated
/// on the fly from the username, so the deck file alone does not reveal it.
/// (Note: usernames are visible in accounts.db, so anyone with both files can
/// rebuild the key; this raises the bar, not over the wall.) Ciphertext is
/// hex-encoded. The original snippet's hardcoded Key/Vector fields and the
/// 3-digit-padded URL encoding are removed.</summary>
public sealed class SimpleAES
{
    private readonly byte[] key;
    private readonly byte[] vector;

    /// <summary>keyHex/vectorHex are hex strings (64 and 32 chars), e.g. produced
    /// by DeriveFromUsername or GenerateEncryptionKey/Vector + Convert.ToHexString.</summary>
    public SimpleAES(string keyHex, string vectorHex)
    {
        key = Convert.FromHexString(keyHex);
        vector = Convert.FromHexString(vectorHex);
    }

    /// <summary>Derives the per-account key + IV from the username: SHA-512 hex
    /// (128 chars), first 64 chars = key, last 32 chars = IV. Deterministic — the
    /// same username always yields the same key material.</summary>
    public static (string KeyHex, string VectorHex) DeriveFromUsername(string username)
    {
        var hash = Convert.ToHexString(SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(username)));
        return (hash[..64], hash[^32..]);
    }

    /// <summary>Generates a 32-byte (256-bit) AES key. Convert with
    /// Convert.ToHexString before storing.</summary>
    static public byte[] GenerateEncryptionKey()
    {
        using var aes = Aes.Create();
        aes.GenerateKey();
        return aes.Key;
    }

    /// <summary>Generates a 16-byte (128-bit) AES IV. Convert with
    /// Convert.ToHexString before storing.</summary>
    static public byte[] GenerateEncryptionVector()
    {
        using var aes = Aes.Create();
        aes.GenerateIV();
        return aes.IV;
    }

    /// <summary>Encrypts text, returns the ciphertext as a hex string.</summary>
    public string EncryptToString(string textValue)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(textValue);
        using var aes = Aes.Create();
        var encrypted = aes.CreateEncryptor(key, vector).TransformFinalBlock(bytes, 0, bytes.Length);
        return Convert.ToHexString(encrypted);
    }

    /// <summary>Decrypts a hex ciphertext produced by EncryptToString.</summary>
    public string DecryptString(string encryptedHex)
    {
        var bytes = Convert.FromHexString(encryptedHex);
        using var aes = Aes.Create();
        var decrypted = aes.CreateDecryptor(key, vector).TransformFinalBlock(bytes, 0, bytes.Length);
        return System.Text.Encoding.UTF8.GetString(decrypted);
    }
}
