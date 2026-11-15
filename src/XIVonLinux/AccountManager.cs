using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Serilog;
using Tomlyn;
using Tomlyn.Model;

namespace XIVonLinux;

/// <summary>Loads/saves accounts.db (SQLite) with one accounts table keyed by
/// user_name PRIMARY KEY — usernames are unique, so there are never two accounts
/// named "test" (Alex's rule). The password side lives in a separate file,
/// accounts.deck: one row per user_name carrying that user's unique AES key + IV
/// (hex strings, generated once per user) and the password ciphertext, stored
/// hex-encoded only when save_password is on; lookups key on user_name. Crash-proof:
/// a corrupt accounts.db/accounts.deck is quarantined as .bad (numbered on collision)
/// and recreated empty — same shape as settings.toml. A legacy accounts.toml from the
/// pre-SQLite era is imported once on first run, first entry wins per username, then
/// renamed accounts.toml.imported.</summary>
public sealed class AccountManager
{
    public ObservableCollection<XivAccount> Accounts { get; private set; } = new();

    private readonly SettingsService settings;

    public AccountManager(SettingsService settings)
    {
        this.settings = settings;
        Load();
    }

    private FileInfo DbFile => this.settings.GetFile("accounts.db");
    private FileInfo DeckFile => this.settings.GetFile("accounts.deck");

    /// <summary>The account the launcher pre-selects: current_account in settings.toml,
    /// matched by username; falls back to the first saved account. The value is stored as
    /// a username, but pre-unique-usernames settings files hold account ids like
    /// "rankyn-True-True" — the username is the part before the first '-'.</summary>
    public XivAccount? CurrentAccount
    {
        get
        {
            var wanted = UserNameOf(this.settings.Settings.CurrentAccountId);
            return this.Accounts.Count > 1
                ? this.Accounts.FirstOrDefault(a => a.UserName == wanted) ?? this.Accounts.FirstOrDefault()
                : this.Accounts.FirstOrDefault();
        }
        set
        {
            this.settings.Settings.CurrentAccountId = value?.UserName;
            this.settings.Save();
        }
    }

    private static string? UserNameOf(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        var dash = stored.IndexOf('-');
        return dash < 0 ? stored : stored[..dash];
    }

    public XivAccount? ByUserName(string userName) =>
        this.Accounts.FirstOrDefault(a => a.UserName == userName.ToLower());

    /// <summary>Unique-username add: an existing username is never duplicated. Same
    /// username with changed flags is an update (UpdateAccountFlags), not a second row.</summary>
    public void AddAccount(XivAccount account)
    {
        var existing = ByUserName(account.UserName);
        if (existing is not null) return;   // keep the saved entry for this username
        this.Accounts.Add(account);
        PersistRow(account);
    }

    /// <summary>Persist the current in-memory flags (OTP/Steam/SavePassword/trial/character)
    /// for an already-saved username.</summary>
    public void UpdateAccountFlags(XivAccount account) => PersistRow(account);

    public void RemoveAccount(XivAccount account)
    {
        this.Accounts.Remove(account);
        if (this.settings.Settings.CurrentAccountId == account.UserName)
            this.settings.Settings.CurrentAccountId = null;
        DeleteRow(account.UserName);
    }

    public void RemoveAllAccounts()
    {
        this.Accounts.Clear();
        this.settings.Settings.CurrentAccountId = null;

        using (var conn = OpenDb())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM accounts;";
            cmd.ExecuteNonQuery();
        }

        using (var deck = OpenDeckDb())
        {
            using var d = deck.CreateCommand();
            d.CommandText = "DELETE FROM passwords;";
            d.ExecuteNonQuery();
        }
    }

    public void UpdateFreeTrial(XivAccount account, bool freeTrial)
    {
        var existing = ByUserName(account.UserName);
        if (existing is not null) existing.IsFreeTrial = freeTrial;
        PersistRow(account);
    }

    // ---------- sqlite ----------

    private SqliteConnection OpenDb()
    {
        var file = DbFile;
        var conn = new SqliteConnection($"Data Source={file.FullName}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS accounts (" +
            "user_name TEXT PRIMARY KEY, " +
            "save_password INTEGER NOT NULL, " +
            "use_steam_service_account INTEGER NOT NULL, " +
            "use_otp INTEGER NOT NULL, " +
            "is_free_trial INTEGER NOT NULL, " +
            "chosen_character_name TEXT NOT NULL, " +
            "chosen_character_world TEXT NOT NULL, " +
            "thumbnail_url TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>accounts.deck: the password side of the store, separate from the
    /// account flags. One row per user_name (PRIMARY KEY) with that user's unique
    /// AES key, IV, and the hex-encoded password ciphertext. Lookups key on
    /// user_name, so the current account's password is found by username.</summary>
    private SqliteConnection OpenDeckDb()
    {
        var file = DeckFile;
        var conn = new SqliteConnection($"Data Source={file.FullName}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS passwords (" +
            "user_name TEXT PRIMARY KEY, " +
            "aes_key TEXT NOT NULL DEFAULT '', " +
            "aes_vector TEXT NOT NULL DEFAULT '', " +
            "encrypted_password TEXT NOT NULL DEFAULT '');";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>One-time migration from the intermediate layout where the crypto
    /// columns lived inside accounts.db: copy any populated rows into accounts.deck
    /// (INSERT OR IGNORE — an existing deck row wins), then DROP the columns. If the
    /// drop fails the columns stay (harmless); the data is already migrated.</summary>
    private void MigrateCryptoToDeck(SqliteConnection accountsConn)
    {
        var cols = new HashSet<string>();
        using (var c = accountsConn.CreateCommand())
        {
            c.CommandText = "SELECT name FROM pragma_table_info('accounts');";
            using var r = c.ExecuteReader();
            while (r.Read())
                cols.Add(r.GetString(0));
        }
        if (!cols.Contains("aes_key"))
            return;

        using var deck = OpenDeckDb();
        using (var copy = accountsConn.CreateCommand())
        {
            copy.CommandText = "SELECT user_name, aes_key, aes_vector, encrypted_password " +
                "FROM accounts WHERE aes_key <> '' OR aes_vector <> '' OR encrypted_password <> ''";
            using var r = copy.ExecuteReader();
            while (r.Read())
            {
                using var ins = deck.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO passwords (user_name, aes_key, aes_vector, encrypted_password)" +
                    " VALUES ($u, $k, $v, $p);";
                ins.Parameters.AddWithValue("$u", r.GetString(0));
                ins.Parameters.AddWithValue("$k", r.GetString(1));
                ins.Parameters.AddWithValue("$v", r.GetString(2));
                ins.Parameters.AddWithValue("$p", r.GetString(3));
                ins.ExecuteNonQuery();
            }
        }

        foreach (var col in new[] { "aes_key", "aes_vector", "encrypted_password" })
        {
            try
            {
                using var a = accountsConn.CreateCommand();
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

    private void PersistRow(XivAccount a, bool ignore = false)
    {
        if (string.IsNullOrEmpty(a.AesKey))
            a.AesKey = Convert.ToHexString(SimpleAES.GenerateEncryptionKey());
        if (string.IsNullOrEmpty(a.AesVector))
            a.AesVector = Convert.ToHexString(SimpleAES.GenerateEncryptionVector());

        var encrypted = a.SavePassword && !string.IsNullOrEmpty(a.Password)
            ? new SimpleAES(a.AesKey, a.AesVector).EncryptToString(a.Password)
            : "";

        // Account flags stay in accounts.db; the crypto row (user_name + key + IV +
        // ciphertext) lives in accounts.deck, keyed by user_name.
        using (var conn = OpenDb())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                (ignore ? "INSERT OR IGNORE INTO accounts" : "INSERT OR REPLACE INTO accounts") +
                " (user_name, save_password, use_steam_service_account, use_otp, is_free_trial," +
                " chosen_character_name, chosen_character_world, thumbnail_url)" +
                " VALUES ($user_name, $save_password, $use_steam, $use_otp, $trial, $char_name, $char_world, $thumb);";
            cmd.Parameters.AddWithValue("$user_name", a.UserName);
            cmd.Parameters.AddWithValue("$save_password", a.SavePassword ? 1 : 0);
            cmd.Parameters.AddWithValue("$use_steam", a.UseSteamServiceAccount ? 1 : 0);
            cmd.Parameters.AddWithValue("$use_otp", a.UseOtp ? 1 : 0);
            cmd.Parameters.AddWithValue("$trial", a.IsFreeTrial ? 1 : 0);
            cmd.Parameters.AddWithValue("$char_name", a.ChosenCharacterName);
            cmd.Parameters.AddWithValue("$char_world", a.ChosenCharacterWorld);
            cmd.Parameters.AddWithValue("$thumb", a.ThumbnailUrl);
            cmd.ExecuteNonQuery();
        }

        using (var deck = OpenDeckDb())
        {
            using var d = deck.CreateCommand();
            d.CommandText =
                (ignore ? "INSERT OR IGNORE INTO passwords" : "INSERT OR REPLACE INTO passwords") +
                " (user_name, aes_key, aes_vector, encrypted_password)" +
                " VALUES ($user_name, $aes_key, $aes_vector, $encrypted_password);";
            d.Parameters.AddWithValue("$user_name", a.UserName);
            d.Parameters.AddWithValue("$aes_key", a.AesKey);
            d.Parameters.AddWithValue("$aes_vector", a.AesVector);
            d.Parameters.AddWithValue("$encrypted_password", encrypted);
            d.ExecuteNonQuery();
        }
    }

    private void DeleteRow(string userName)
    {
        using (var conn = OpenDb())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM accounts WHERE user_name = $user_name;";
            cmd.Parameters.AddWithValue("$user_name", userName);
            cmd.ExecuteNonQuery();
        }

        using (var deck = OpenDeckDb())
        {
            using var d = deck.CreateCommand();
            d.CommandText = "DELETE FROM passwords WHERE user_name = $user_name;";
            d.Parameters.AddWithValue("$user_name", userName);
            d.ExecuteNonQuery();
        }
    }

    private void Load()
    {
        this.Accounts = new ObservableCollection<XivAccount>();

        SqliteConnection conn;
        try
        {
            conn = OpenDb();
        }
        catch (SqliteException ex)
        {
            Log.Error(ex, "accounts.db is corrupt; quarantining as accounts.db.bad and recreating it empty");
            Quarantine(DbFile);
            conn = OpenDb();
        }

        using (conn)
        {
            ImportLegacyToml(conn);
            MigrateCryptoToDeck(conn);

            // Crypto material lives in accounts.deck, keyed by user_name.
            SqliteConnection deck;
            try
            {
                deck = OpenDeckDb();
            }
            catch (SqliteException ex)
            {
                Log.Error(ex, "accounts.deck is corrupt; quarantining as accounts.deck.bad and recreating it empty");
                Quarantine(DeckFile);
                deck = OpenDeckDb();
            }

            var crypto = new Dictionary<string, (string Key, string Vector, string Cipher)>();
            using (deck)
            {
                using var q = deck.CreateCommand();
                q.CommandText = "SELECT user_name, aes_key, aes_vector, encrypted_password FROM passwords;";
                using var r = q.ExecuteReader();
                while (r.Read())
                    crypto[r.GetString(0)] = (r.GetString(1), r.GetString(2), r.GetString(3));
            }

            using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT user_name, save_password, use_steam_service_account, use_otp, is_free_trial, chosen_character_name, chosen_character_world, thumbnail_url FROM accounts ORDER BY user_name;";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var acc = new XivAccount(r.GetString(0));
                    acc.SavePassword = r.GetInt64(1) != 0;
                    acc.UseSteamServiceAccount = r.GetInt64(2) != 0;
                    acc.UseOtp = r.GetInt64(3) != 0;
                    acc.IsFreeTrial = r.GetInt64(4) != 0;
                    acc.ChosenCharacterName = r.GetString(5);
                    acc.ChosenCharacterWorld = r.GetString(6);
                    acc.ThumbnailUrl = r.GetString(7);

                    if (crypto.TryGetValue(acc.UserName, out var c))
                    {
                        acc.AesKey = c.Key;
                        acc.AesVector = c.Vector;
                        if (acc.SavePassword && c.Cipher.Length > 0)
                        {
                            try
                            {
                                acc.Password = new SimpleAES(acc.AesKey, acc.AesVector).DecryptString(c.Cipher);
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "Could not decrypt the stored password for '{User}' — starting with an empty password field", acc.UserName);
                            }
                        }
                    }

                    this.Accounts.Add(acc);
                }
            }
        }
    }

    /// <summary>One-time import of the pre-SQLite accounts.toml: INSERT OR IGNORE so the
    /// first entry wins when the legacy file had the same username twice (old ids allowed
    /// username+flag variants). The legacy file is renamed accounts.toml.imported
    /// (numbered on collision) so it runs once.</summary>
    private void ImportLegacyToml(SqliteConnection conn)
    {
        var legacy = this.settings.GetFile("accounts.toml");
        if (!File.Exists(legacy.FullName))
            return;

        var imported = 0;
        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(legacy.FullName));
            if (model.TryGetValue("account", out var raw))
            {
                var tables = raw switch
                {
                    TomlTableArray tarr => tarr.Cast<object>(),
                    TomlArray arr => arr.Cast<object>(),
                    _ => Enumerable.Empty<object>(),
                };
                foreach (var t in tables.OfType<TomlTable>())
                {
                    var acc = new XivAccount(GetStr(t, "user_name") ?? "unknown");
                    acc.SavePassword = GetBool(t, "save_password");
                    acc.UseSteamServiceAccount = GetBool(t, "use_steam_service_account");
                    acc.UseOtp = GetBool(t, "use_otp");
                    acc.IsFreeTrial = GetBool(t, "is_free_trial");
                    acc.ChosenCharacterName = GetStr(t, "chosen_character_name") ?? "";
                    acc.ChosenCharacterWorld = GetStr(t, "chosen_character_world") ?? "";
                    acc.ThumbnailUrl = GetStr(t, "thumbnail_url") ?? "";

                    PersistRow(acc, ignore: true);
                    imported++;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "legacy accounts.toml is malformed; skipping import");
        }

        var dest = Path.Combine(legacy.DirectoryName!, "accounts.toml.imported");
        var n = 1;
        while (File.Exists(dest))
            dest = Path.Combine(legacy.DirectoryName!, $"accounts.toml.imported.{n++}");
        File.Move(legacy.FullName, dest);
        Log.Information("Imported {Count} legacy accounts from accounts.toml into accounts.db", imported);
    }

    private static void Quarantine(FileInfo file)
    {
        var badPath = Path.Combine(file.DirectoryName!, file.Name + ".bad");
        var n = 1;
        while (File.Exists(badPath))
            badPath = Path.Combine(file.DirectoryName!, $"{file.Name}.bad.{n++}");
        File.Move(file.FullName, badPath);
    }

    private static string? GetStr(TomlTable t, string key) =>
        t.ContainsKey(key) ? t[key] as string : null;

    private static bool GetBool(TomlTable t, string key) =>
        t.ContainsKey(key) && t[key] is bool b && b;
}
