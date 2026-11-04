using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Serilog;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace XIVonLinux;

/// <summary>Loads/saves accounts.toml with Tomlyn (array-of-tables [[account]]),
/// hand-written like settings.toml. A malformed accounts.toml is quarantined as
/// accounts.toml.bad (numbered on collision) and the launcher starts with an
/// empty account list — same crash-proof shape as settings.toml.</summary>
public sealed class AccountManager
{
    public ObservableCollection<XivAccount> Accounts { get; private set; } = new();

    private readonly SettingsService settings;

    public AccountManager(SettingsService settings)
    {
        this.settings = settings;
        Load();
    }

    /// <summary>The account the launcher pre-selects: current_account in settings.toml,
    /// matched by Id; falls back to the first saved account.</summary>
    public XivAccount? CurrentAccount
    {
        get => this.Accounts.Count > 1
            ? this.Accounts.FirstOrDefault(a => a.Id == this.settings.Settings.CurrentAccountId)
            : this.Accounts.FirstOrDefault();
        set
        {
            this.settings.Settings.CurrentAccountId = value?.Id;
            this.settings.Save();
        }
    }

    public XivAccount? ByUserName(string userName) =>
        this.Accounts.FirstOrDefault(a => a.UserName == userName.ToLower());

    public void AddAccount(XivAccount account)
    {
        var existing = this.Accounts.FirstOrDefault(a => a.Id == account.Id);
        if (existing is not null) return;   // same user + flags: keep the saved entry
        this.Accounts.Add(account);
        Save();
    }

    public void RemoveAccount(XivAccount account)
    {
        this.Accounts.Remove(account);
        if (this.settings.Settings.CurrentAccountId == account.Id)
            this.settings.Settings.CurrentAccountId = null;
        Save();
    }

    public void RemoveAllAccounts()
    {
        this.Accounts.Clear();
        this.settings.Settings.CurrentAccountId = null;
        Save();
    }

    public void UpdateFreeTrial(XivAccount account, bool freeTrial)
    {
        var existing = this.Accounts.FirstOrDefault(a => a.Id == account.Id);
        if (existing is not null) existing.IsFreeTrial = freeTrial;
        Save();
    }

    private FileInfo AccountFile => this.settings.GetFile("accounts.toml");

    public void Save()
    {
        var sb = new StringBuilder();
        foreach (var a in this.Accounts)
        {
            sb.AppendLine("[[account]]");
            sb.AppendLine($"user_name = {TomlString(a.UserName)}");
            sb.AppendLine($"save_password = {a.SavePassword.ToString().ToLowerInvariant()}");
            sb.AppendLine($"use_steam_service_account = {a.UseSteamServiceAccount.ToString().ToLowerInvariant()}");
            sb.AppendLine($"use_otp = {a.UseOtp.ToString().ToLowerInvariant()}");
            sb.AppendLine($"is_free_trial = {a.IsFreeTrial.ToString().ToLowerInvariant()}");
            sb.AppendLine($"chosen_character_name = {TomlString(a.ChosenCharacterName)}");
            sb.AppendLine($"chosen_character_world = {TomlString(a.ChosenCharacterWorld)}");
            sb.AppendLine($"thumbnail_url = {TomlString(a.ThumbnailUrl)}");
            sb.AppendLine();
        }
        File.WriteAllText(AccountFile.FullName, sb.ToString());
    }

    private void Load()
    {
        var file = AccountFile;
        if (!File.Exists(file.FullName))
        {
            Save();
            return;
        }

        TomlTable model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file.FullName));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "accounts.toml is malformed; quarantining as accounts.toml.bad");
            var badPath = Path.Combine(file.DirectoryName!, "accounts.toml.bad");
            var n = 1;
            while (File.Exists(badPath))
                badPath = Path.Combine(file.DirectoryName!, $"accounts.toml.bad.{n++}");
            File.Move(file.FullName, badPath);
            this.Accounts = new ObservableCollection<XivAccount>();
            return;
        }

        this.Accounts = new ObservableCollection<XivAccount>();
        // Tomlyn deserializes top-level [[account]] as TomlTableArray, not TomlArray —
        // an `is TomlArray` check silently loads zero accounts.
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
                this.Accounts.Add(acc);
            }
        }
    }

    private static string? GetStr(TomlTable t, string key) =>
        t.ContainsKey(key) ? t[key] as string : null;

    private static bool GetBool(TomlTable t, string key) =>
        t.ContainsKey(key) && t[key] is bool b && b;

    private static string TomlString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
