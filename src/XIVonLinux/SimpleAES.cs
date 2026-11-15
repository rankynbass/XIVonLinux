using System;
using System.Security.Cryptography;

namespace XIVonLinux;

/// <summary>Per-account AES-CBC (256-bit key, 128-bit IV) obfuscation of saved
/// passwords. NOT security hardening — the key lives next to the ciphertext on the
/// same disk; it is obfuscation so the password is never stored as plain text
/// (Alex's rule). Each account gets its own unique key + vector generated once at
/// registration (GenerateEncryptionKey/GenerateEncryptionVector), stored as hex
/// strings in the accounts table; SimpleAES is constructed per account from those
/// strings. Ciphertext is hex-encoded. The original snippet's hardcoded
/// Key/Vector fields and the 3-digit-padded URL encoding are removed.</summary>
public sealed class SimpleAES
{
    private readonly byte[] key;
    private readonly byte[] vector;

    /// <summary>keyHex/vectorHex are the hex strings produced from
    /// GenerateEncryptionKey()/GenerateEncryptionVector() via Convert.ToHexString.</summary>
    public SimpleAES(string keyHex, string vectorHex)
    {
        key = Convert.FromHexString(keyHex);
        vector = Convert.FromHexString(vectorHex);
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
