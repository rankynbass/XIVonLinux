using KeySharp;

using Serilog;

namespace XIVonLinux;

/// <summary>Password storage in the freedesktop secret service (org.freedesktop.secrets
/// — gnome-keyring, KWallet, KeeShare) via KeySharp. No encryption is done here: the
/// secret service itself protects the entries. Mirrors XIVLauncher.Core's
/// KeychainSecretProvider shape (package/service labels + dummy-write availability
/// probe; the dummy write unlocks the keyring, libsecret quirk crbug.com/660005).
/// IsAvailable is false when no secret service responds — the launcher then falls
/// back to the deck provider.</summary>
public sealed class KeychainSecretProvider : ISecretProvider
{
    public const string PACKAGE = "app.xivonlinux";
    public const string SERVICE = "SEID";

    public bool IsAvailable { get; }

    public KeychainSecretProvider()
    {
        IsAvailable = SetDummyAndCheck();
    }

    /// <summary>Probes the keyring by writing and reading back a dummy secret.
    /// Without this libsecret may leave the keyring locked.</summary>
    private static bool SetDummyAndCheck()
    {
        const string DUMMY_SVC = "XIVonLinux Safe Storage Control";
        const string DUMMY_NAME = "XIVonLinux";
        const string DUMMY_PW = "Honi soit qui mal y pense";

        try
        {
            Log.Verbose("Testing setting dummy passwords to keyring");
            Keyring.SetPassword(PACKAGE, DUMMY_SVC, DUMMY_NAME, DUMMY_PW);

            Log.Verbose("Testing getting dummy password from keyring");
            var saved = Keyring.GetPassword(PACKAGE, DUMMY_SVC, DUMMY_NAME);
            return saved == DUMMY_PW;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not init the keyring");
        }

        return false;
    }

    public string? GetPassword(string accountName)
    {
        try
        {
            return Keyring.GetPassword(PACKAGE, SERVICE, accountName);
        }
        catch (KeyringException ex)
        {
            if (ex.Type == ErrorType.NotFound)
                return null;

            throw;
        }
    }

    public void SavePassword(string accountName, string password)
    {
        Keyring.SetPassword(PACKAGE, SERVICE, accountName, password);
    }

    public void DeletePassword(string accountName)
    {
        try
        {
            Keyring.DeletePassword(PACKAGE, SERVICE, accountName);
        }
        catch (KeyringException ex)
        {
            // KeySharp 1.0.5 quirks: successful deletes report Type=NoError;
            // deleting a non-existent entry reports NotFound, which is a
            // success for us (nothing to delete).
            if (ex.Type != ErrorType.NoError && ex.Type != ErrorType.NotFound)
                throw;
        }
    }
}
