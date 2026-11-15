namespace XIVonLinux;

/// <summary>Password storage backend. Saved passwords go through exactly one
/// provider: the OS keyring (KeychainSecretProvider), the accounts.deck
/// encrypted file (DeckSecretProvider), or nowhere (DummySecretProvider).
/// Keyring and deck are alternatives — keyring needs no encryption because the
/// secret service protects the entries; the deck exists for when no
/// org.freedesktop.secrets service is available.</summary>
public interface ISecretProvider
{
    /// <summary>The stored password for this account name, or null if none.</summary>
    public string? GetPassword(string accountName);

    public void SavePassword(string accountName, string password);

    public void DeletePassword(string accountName);
}
