namespace XIVonLinux;

/// <summary>A saved login account, persisted as one [[account]] table in
/// ~/.xivonlinux/accounts.toml. The password is NOT stored on disk (Alex's rule:
/// credentials are never persisted) — it is held in memory on this object only,
/// populated when the user logs in, and never written by AccountManager.Save().</summary>
public sealed class XivAccount
{
    public string Id => $"{UserName}-{UseOtp}-{UseSteamServiceAccount}";

    public override string ToString() => Id;

    public string UserName { get; set; } = string.Empty;

    /// <summary>In-memory only: never written to accounts.toml (credentials rule).</summary>
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
