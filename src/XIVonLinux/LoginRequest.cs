namespace XIVonLinux;

/// <summary>App-side login request DTO: built from the login UI fields.
/// Password/OTP are in-memory only; the service never persists them.</summary>
public sealed class LoginRequest
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string OneTimePassword { get; init; } = "";
    public bool SteamAccount { get; init; }
    public bool FreeTrial { get; init; }
}
