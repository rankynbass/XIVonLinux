namespace XIVLauncher.Common.Linux;

/// <summary>Library-side login request DTO: the app passes plain values in, never the
/// app's settings object. Password/OTP are in-memory only; the service never persists them.</summary>
public sealed class LoginRequest
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string OneTimePassword { get; init; } = "";
    public bool SteamAccount { get; init; }
    public bool FreeTrial { get; init; }
}
