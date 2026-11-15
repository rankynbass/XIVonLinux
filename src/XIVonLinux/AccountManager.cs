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
/// named "test" (Alex's rule). Account flags only live in accounts.db; passwords
/// go through exactly one ISecretProvider backend (SecretProvider): the OS
/// keyring (KeychainSecretProvider, no encryption — the service protects
/// entries) or the encrypted accounts.deck file (DeckSecretProvider). Keyring
/// and deck are ALTERNATIVES — keyring does not work with the deck, which is
/// why the deck exists: when no org.freedesktop.secrets service answers the
/// dummy probe, the launcher falls back to the deck. XL_SECRET_PROVIDER env
/// selects KEYRING (default) / FILE / NONE. Crash-proof: a corrupt accounts.db
/// is quarantined as .bad (numbered on collision) and recreated empty — same
/// shape as settings.toml. A legacy accounts.toml from the pre-SQLite era is
/// imported once on first run, first entry wins per username, then renamed
/// accounts.toml.imported.</summary>
public sealed class AccountManager
{
    public ObservableCollection<XivAccount> Accounts { get; private set; } = new();

    private readonly SettingsService settings;

    /// <summary>The active password backend (keyring, deck, or dummy).</summary>
    public ISecretProvider SecretProvider { get; }

    public AccountManager(SettingsService settings)
    {
        this.settings = settings;
        this.SecretProvider = CreateSecretProvider(settings);
        Load();
    }

    /// <summary>Selects the password backend. Default: keyring, falling back to
    /// the deck when the secret service is unavailable (Alex's rule: keychain
    /// and deck are alternatives). XL_SECRET_PROVIDER=KEYRING|FILE|NONE overrides.</summary>
    private static ISecretProvider CreateSecretProvider(SettingsService settings)
    {
        var choice = (Environment.GetEnvironmentVariable("XL_SECRET_PROVIDER") ?? "KEYRING").ToUpperInvariant();

        switch (choice)
        {
            case "KEYRING":
                var keyring = new KeychainSecretProvider();
                if (keyring.IsAvailable)
                {
                    Log.Information("Password backend: keyring");
                    return keyring;
                }

                Log.Warning("No org.freedesktop.secrets provider available; falling back to the deck backend");
                return new DeckSecretProvider(settings);

            case "FILE":
                Log.Information("Password backend: deck (file)");
                return new DeckSecretProvider(settings);

            case "NONE":
                Log.Warning("Password backend: none — saved passwords will not persist");
                return new DummySecretProvider();

            default:
                throw new ArgumentException($"Invalid secret provider: {choice}");
        }
    }

    private FileInfo DbFile => this.settings.GetFile("accounts.db");

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
        foreach (var acc in this.Accounts)
            this.SecretProvider.DeletePassword(acc.UserName);

        this.Accounts.Clear();
        this.settings.Settings.CurrentAccountId = null;

        using var conn = OpenDb();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts;";
        cmd.ExecuteNonQuery();
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

    private void PersistRow(XivAccount a, bool ignore = false)
    {
        // Account flags stay in accounts.db; the password goes through the active
        // secret provider (keyring / deck / dummy).
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

        if (a.SavePassword && !string.IsNullOrEmpty(a.Password))
            this.SecretProvider.SavePassword(a.UserName, a.Password);
        else
            this.SecretProvider.DeletePassword(a.UserName);
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

        this.SecretProvider.DeletePassword(userName);
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

                    // Password comes from the active secret provider; deck migrations
                    // already ran in DeckSecretProvider's constructor.
                    if (acc.SavePassword)
                        acc.Password = this.SecretProvider.GetPassword(acc.UserName) ?? "";

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
