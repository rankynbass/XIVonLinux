using System.Text;

using Microsoft.Data.Sqlite;

using Serilog;

using XIVonLinux.Settings;

namespace XIVonLinux.Secrets;

/// <summary>Fallback password backend: passwords are stored as hex AES-CBC
/// ciphertext in the separate SQLite file ~/.xivonlinux/accounts.deck, keyed by
/// user_name. The key + IV are DERIVED from the username, never stored:
/// SimpleAES.DeriveFromUsername (SHA-512 hex of the username, 128 chars;
/// first 64 = AES-256 key, last 32 = IV), so the deck alone does not reveal key
/// material. Used when the secret service is unavailable — keyring and deck are
/// alternative backends (keyring needs none of this because the service
/// protects its entries). Owns all deck SQL; crash-proof: a corrupt deck is
/// quarantined as accounts.deck.bad and recreated empty.</summary>
public sealed class DeckSecretProvider : ISecretProvider
{
    private readonly FileInfo deckFile;
    private readonly FileInfo accountsDbFile;

    public DeckSecretProvider(SettingsService settings)
    {
        deckFile = settings.GetConfigFile("accounts.deck");
        accountsDbFile = settings.GetConfigFile("accounts.db");

        EnsureDeckSchema();
        MigrateCryptoFromAccountsDb();
    }

    private SqliteConnection OpenDeckDb()
    {
        Directory.CreateDirectory(deckFile.DirectoryName!);
        var conn = new SqliteConnection($"Data Source={deckFile.FullName}");
        conn.Open();
        return conn;
    }

    /// <summary>Creates the deck with the cipher-only schema. Old decks stored
    /// per-account aes_key/aes_vector columns; every ciphertext is decrypted
    /// under the old stored key and re-encrypted under the username-derived key,
    /// then the table is rebuilt without the key columns. A corrupt deck is
    /// quarantined as .bad and recreated empty.</summary>
    private void EnsureDeckSchema()
    {
        SqliteConnection conn;
        try
        {
            conn = OpenDeckDb();
        }
        catch (SqliteException ex)
        {
            Log.Error(ex, "accounts.deck is corrupt; quarantining as accounts.deck.bad and recreating it empty");
            Quarantine(deckFile);
            conn = OpenDeckDb();
        }

        using (conn)
        {
            var cols = new HashSet<string>();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT name FROM pragma_table_info('passwords');";
                using var r = c.ExecuteReader();
                while (r.Read())
                    cols.Add(r.GetString(0));
            }

            if (cols.Count == 0)
            {
                using var create = conn.CreateCommand();
                create.CommandText =
                    "CREATE TABLE IF NOT EXISTS passwords (user_name TEXT PRIMARY KEY, encrypted_password TEXT NOT NULL DEFAULT '');";
                create.ExecuteNonQuery();
                return;
            }

            if (cols.Contains("aes_key"))
            {
                var rows = new List<(string User, string Key, string Vec, string Cipher)>();
                using (var q = conn.CreateCommand())
                {
                    q.CommandText = "SELECT user_name, aes_key, aes_vector, encrypted_password FROM passwords;";
                    using var r = q.ExecuteReader();
                    while (r.Read())
                        rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
                }

                using (var create = conn.CreateCommand())
                {
                    create.CommandText = "CREATE TABLE IF NOT EXISTS passwords_new (user_name TEXT PRIMARY KEY, encrypted_password TEXT NOT NULL DEFAULT '');";
                    create.ExecuteNonQuery();
                }
                foreach (var (user, key, vec, cipher) in rows)
                {
                    var migrated = "";
                    if (cipher.Length > 0)
                    {
                        try
                        {
                            var plain = new SimpleAES(key, vec).DecryptString(cipher);
                            var (dk, dv) = SimpleAES.DeriveFromUsername(user);
                            migrated = new SimpleAES(dk, dv).EncryptToString(plain);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Deck row for '{User}' cannot be decrypted with the stored key; password starts blank", user);
                        }
                    }

                    using var ins = conn.CreateCommand();
                    ins.CommandText = "DELETE FROM passwords_new WHERE user_name = $u; INSERT INTO passwords_new (user_name, encrypted_password) VALUES ($u, $p);";
                    ins.Parameters.AddWithValue("$u", user);
                    ins.Parameters.AddWithValue("$p", migrated);
                    ins.ExecuteNonQuery();
                }

                using (var drop = conn.CreateCommand())
                {
                    drop.CommandText = "DROP TABLE passwords;";
                    drop.ExecuteNonQuery();
                }
                using (var rename = conn.CreateCommand())
                {
                    rename.CommandText = "ALTER TABLE passwords_new RENAME TO passwords;";
                    rename.ExecuteNonQuery();
                }
                Log.Information("Migrated accounts.deck: re-encrypted under username-derived keys, dropped stored key columns");
            }
        }
    }

    /// <summary>One-time migration from the intermediate layout where the crypto
    /// columns lived inside accounts.db: copy populated rows into accounts.deck
    /// (INSERT OR IGNORE — an existing deck row wins), then DROP the columns. If
    /// the drop fails the columns stay (harmless); the data is already migrated.</summary>
    private void MigrateCryptoFromAccountsDb()
    {
        if (!File.Exists(accountsDbFile.FullName))
            return;

        using var conn = new SqliteConnection($"Data Source={accountsDbFile.FullName}");
        conn.Open();

        var cols = new HashSet<string>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT name FROM pragma_table_info('accounts');";
            using var r = c.ExecuteReader();
            while (r.Read())
                cols.Add(r.GetString(0));
        }
        if (!cols.Contains("aes_key"))
            return;

        using var deck = OpenDeckDb();
        using (var copy = conn.CreateCommand())
        {
            copy.CommandText = "SELECT user_name, aes_key, aes_vector, encrypted_password " +
                "FROM accounts WHERE aes_key <> '' OR aes_vector <> '' OR encrypted_password <> ''";
            using var r = copy.ExecuteReader();
            while (r.Read())
            {
                using var ins = deck.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO passwords (user_name, encrypted_password) VALUES ($u, $p);";
                ins.Parameters.AddWithValue("$u", r.GetString(0));
                // Re-encrypt under the username-derived key; the old stored key is not copied.
                var key = r.GetString(1);
                var vec = r.GetString(2);
                var cipher = r.GetString(3);
                var plain = cipher.Length > 0 ? new SimpleAES(key, vec).DecryptString(cipher) : "";
                ins.Parameters.AddWithValue("$p", plain.Length > 0 ? EncryptFor(r.GetString(0), plain) : "");
                ins.ExecuteNonQuery();
            }
        }

        foreach (var col in new[] { "aes_key", "aes_vector", "encrypted_password" })
        {
            try
            {
                using var a = conn.CreateCommand();
                a.CommandText = $"ALTER TABLE accounts DROP COLUMN {col};";
                a.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Log.Warning(ex, "Could not drop {Col} from accounts.db; the data is already in accounts.deck", col);
            }
        }
        Log.Information("Migrated account password crypto data from accounts.db into accounts.deck");
    }

    private static string EncryptFor(string user, string plain)
    {
        var (k, v) = SimpleAES.DeriveFromUsername(user);
        return new SimpleAES(k, v).EncryptToString(plain);
    }

    private static string DecryptFor(string user, string cipher)
    {
        var (k, v) = SimpleAES.DeriveFromUsername(user);
        return new SimpleAES(k, v).DecryptString(cipher);
    }

    public string? GetPassword(string accountName)
    {
        using var conn = OpenDeckDb();
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT encrypted_password FROM passwords WHERE user_name = $u;";
        q.Parameters.AddWithValue("$u", accountName);
        var cipher = q.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(cipher))
            return null;

        try
        {
            return DecryptFor(accountName, cipher);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deck password for '{User}' could not be decrypted", accountName);
            return null;
        }
    }

    public void SavePassword(string accountName, string password)
    {
        using var conn = OpenDeckDb();
        using var c = conn.CreateCommand();
        c.CommandText = "INSERT OR REPLACE INTO passwords (user_name, encrypted_password) VALUES ($u, $p);";
        c.Parameters.AddWithValue("$u", accountName);
        c.Parameters.AddWithValue("$p", string.IsNullOrEmpty(password) ? "" : EncryptFor(accountName, password));
        c.ExecuteNonQuery();
    }

    public void DeletePassword(string accountName)
    {
        using var conn = OpenDeckDb();
        using var c = conn.CreateCommand();
        c.CommandText = "DELETE FROM passwords WHERE user_name = $u;";
        c.Parameters.AddWithValue("$u", accountName);
        c.ExecuteNonQuery();
    }

    private static void Quarantine(FileInfo file)
    {
        var badPath = Path.Combine(file.DirectoryName!, file.Name + ".bad");
        var n = 1;
        while (File.Exists(badPath))
            badPath = Path.Combine(file.DirectoryName!, $"{file.Name}.bad.{n++}");
        File.Move(file.FullName, badPath);
    }
}

/// <summary>Stores nothing. Used when the user explicitly selects no backend
/// (XIV_SECRET_PROVIDER=NONE), so saved-password UI can hide itself.</summary>
public sealed class DummySecretProvider : ISecretProvider
{
    public string? GetPassword(string accountName) => null;

    public void SavePassword(string accountName, string password) { }

    public void DeletePassword(string accountName) { }
}
