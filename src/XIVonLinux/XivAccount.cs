using XIVonLinux.Secrets;

namespace XIVonLinux;

/// <summary>A saved login account, persisted as one row in the accounts table of
/// ~/.xivonlinux/accounts.db (user_name PRIMARY KEY — usernames are unique). The
/// password is persisted only when SavePassword is true, through the active
/// ISecretProvider backend (OS keyring, or the encrypted accounts.deck file);
/// otherwise it stays in memory only, populated when the user logs in.</summary>
public sealed class XivAccount
{
    public string Id => $"{UserName}-{UseOtp}-{UseSteamServiceAccount}";

    public override string ToString() => Id;

    public string UserName { get; set; } = string.Empty;

    /// <summary>Plain-text password held in memory. When SavePassword is true it
    /// is persisted through the active secret provider (keyring or deck).</summary>
    public string Password { get; set; } = string.Empty;

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
