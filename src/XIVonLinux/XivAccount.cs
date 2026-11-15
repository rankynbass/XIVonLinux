namespace XIVonLinux;

/// <summary>A saved login account, persisted as one row in the accounts table of
/// ~/.xivonlinux/accounts.db (user_name PRIMARY KEY — usernames are unique). The
/// password is persisted only when SavePassword is true, encrypted with the account's
/// per-user AesKey/AesVector (obfuscation); otherwise it stays in memory only,
/// populated when the user logs in.</summary>
public sealed class XivAccount
{
    public string Id => $"{UserName}-{UseOtp}-{UseSteamServiceAccount}";

    public override string ToString() => Id;

    public string UserName { get; set; } = string.Empty;

    /// <summary>Plain-text password held in memory. When SavePassword is true it is
    /// persisted to accounts.db encrypted with this account's AesKey/AesVector
    /// (obfuscation, not security — the key sits beside the ciphertext).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Per-account AES key + IV as hex strings, generated once at account
    /// registration, stored in accounts.deck; used to encrypt/decrypt Password.</summary>
    public string AesKey { get; set; } = string.Empty;
    public string AesVector { get; set; } = string.Empty;

    public bool SavePassword { get; set; }
    public bool UseSteamServiceAccount { get; set; }
    public bool UseOtp { get; set; }
    public bool IsFreeTrial { get; set; }

    public string ChosenCharacterName { get; set; } = string.Empty;
    public string ChosenCharacterWorld { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;

    public XivAccount(string userName)
    {
        UserName = userName.ToLower();
    }
}
