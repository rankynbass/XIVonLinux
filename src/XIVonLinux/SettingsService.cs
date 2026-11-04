using System;
using System.IO;
using System.Text;
using Serilog;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using XIVLauncher.Common;

namespace XIVonLinux;

/// <summary>
/// User-configurable launcher settings, persisted as ~/.xivonlinux/settings.toml
/// via XIVLauncher.Common.Storage("xivonlinux"), split into [game], [proton],
/// [dalamud] and [login] sections. Credentials are NOT persisted to disk —
/// Password/OTP are UI-only fields, cleared on save.
/// </summary>
public sealed class LaunchSettings
{
    // Game tab
    public string GamePath { get; set; } = "";        // game install folder
    public string ConfigPath { get; set; } = "";      // character data folder (ffxivConfig)
    public string PatchPath { get; set; } = "";       // downloaded patches folder
    public string Language { get; set; } = "English"; // game client language: Japanese/English/German/French

    // Proton tab
    public string ProtonPath { get; set; } = "";
    public string PrefixPath { get; set; } = "";      // WINEPREFIX root; prefix dir = {PrefixPath}/pfx
    public bool EnableDxvk { get; set; } = true;       // DXVK preset for WINEDLLOVERRIDES
    public string ExtraWineDllOverrides { get; set; } = ""; // appended after the DXVK preset

    // Dalamud tab
    public bool DalamudEnabled { get; set; } = true;

    // Login (not a tab; persisted alongside the tabs)
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";        // UI-only, never persisted
    public string OneTimePassword { get; set; } = ""; // UI-only, never persisted
    public bool SteamAccount { get; set; } = true;
    public bool FreeTrial { get; set; }
    public string? CurrentAccountId { get; set; }     // accounts.toml account the launcher pre-selects
}

/// <summary>
/// Loads/saves LaunchSettings with Tomlyn. settings.toml is sectioned to match the
/// tabbed settings UI: [game], [proton], [dalamud], [login]; keys are snake_case.
/// A section absent from an older flat settings.toml falls back to the legacy
/// top-level key, so existing settings survive the format change.
/// Defaults: Proton = GE-Proton 11-6 under Steam compatibilitytools.d,
/// Game = ~/Games/final-fantasy-xiv, Patches = the storage 'patches' folder.
/// </summary>
public sealed class SettingsService
{
    private readonly Storage storage;

    public SettingsService()
    {
        this.storage = new Storage("xivonlinux");
        this.Settings = Load();
        ApplyDefaults();
        // Fresh copy on disk after missing/malformed/quarantined settings (and any
        // newly defaulted entries); a no-op write when the file is already complete.
        Save();
    }

    public LaunchSettings Settings { get; private set; }

    /// <summary>The ~/.xivonlinux root, created on first use.</summary>
    public DirectoryInfo Root => this.storage.Root;

    /// <summary>The XIVLauncher.Common storage; passed to library services as values
    /// (GetFile/GetFolder), not as the settings object.</summary>
    public Storage Storage => this.storage;

    /// <summary>A subfolder of ~/.xivonlinux, created if missing (e.g. "patches").</summary>
    public DirectoryInfo GetFolder(string name) => this.storage.GetFolder(name);

    /// <summary>A file path in ~/.xivonlinux (e.g. "aria2.log").</summary>
    public FileInfo GetFile(string name) => this.storage.GetFile(name);

    public void Save()
    {
        var sb = new StringBuilder();

        sb.AppendLine("[game]");
        sb.AppendLine($"game_path = {TomlString(this.Settings.GamePath)}");
        sb.AppendLine($"config_path = {TomlString(this.Settings.ConfigPath)}");
        sb.AppendLine($"patch_path = {TomlString(this.Settings.PatchPath)}");
        sb.AppendLine($"language = {TomlString(this.Settings.Language)}");
        sb.AppendLine();

        sb.AppendLine("[proton]");
        sb.AppendLine($"proton_path = {TomlString(this.Settings.ProtonPath)}");
        sb.AppendLine($"prefix_path = {TomlString(this.Settings.PrefixPath)}");
        sb.AppendLine($"enable_dxvk = {this.Settings.EnableDxvk.ToString().ToLowerInvariant()}");
        sb.AppendLine($"extra_wine_dll_overrides = {TomlString(this.Settings.ExtraWineDllOverrides)}");
        sb.AppendLine();

        sb.AppendLine("[dalamud]");
        sb.AppendLine($"enabled = {this.Settings.DalamudEnabled.ToString().ToLowerInvariant()}");
        sb.AppendLine();

        sb.AppendLine("[login]");
        sb.AppendLine($"username = {TomlString(this.Settings.Username)}");
        sb.AppendLine($"steam_account = {this.Settings.SteamAccount.ToString().ToLowerInvariant()}");
        sb.AppendLine($"free_trial = {this.Settings.FreeTrial.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrEmpty(this.Settings.CurrentAccountId))
            sb.AppendLine($"current_account = {TomlString(this.Settings.CurrentAccountId)}");

        var file = this.storage.GetFile("settings.toml");
        File.WriteAllText(file.FullName, sb.ToString());
    }

    private LaunchSettings Load()
    {
        var s = new LaunchSettings();
        var file = this.storage.GetFile("settings.toml");
        if (!File.Exists(file.FullName))
            return s;

        TomlTable model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file.FullName));
        }
        catch (Exception ex)
        {
            // Malformed TOML must not crash the launcher: quarantine the file as
            // settings.toml.bad (numbered on collision) and start from defaults;
            // ApplyDefaults + the startup Save() write a fresh settings.toml.
            Log.Error(ex, "settings.toml is malformed; quarantining as settings.toml.bad");
            var badPath = Path.Combine(file.DirectoryName!, "settings.toml.bad");
            var n = 1;
            while (File.Exists(badPath))
                badPath = Path.Combine(file.DirectoryName!, $"settings.toml.bad.{n++}");
            File.Move(file.FullName, badPath);
            return s;
        }

        // Sectioned keys; TomlTable indexer throws on missing keys, so look keys up
        // via ContainsKey. A key present with the wrong type, and a missing section,
        // fall back to the legacy flat top-level key, then to the property default —
        // pre-section settings.toml files migrate on the next Save().
        TomlTable? Sec(string name) => model.ContainsKey(name) && model[name] is TomlTable t ? t : null;

        string? Get(TomlTable? sec, string key, string legacyKey)
        {
            if (sec is not null && sec.ContainsKey(key) && sec[key] is string v)
                return v;
            if (model.ContainsKey(legacyKey) && model[legacyKey] is string lv)
                return lv;
            return null;
        }

        bool GetBool(TomlTable? sec, string key, string legacyKey, bool def)
        {
            if (sec is not null && sec.ContainsKey(key) && sec[key] is bool b)
                return b;
            if (model.ContainsKey(legacyKey) && model[legacyKey] is bool lb)
                return lb;
            return def;
        }

        var game = Sec("game");
        var proton = Sec("proton");
        var dalamud = Sec("dalamud");
        var login = Sec("login");

        if (Get(game, "game_path", "game_location") is string gp) s.GamePath = gp;
        if (Get(game, "config_path", "config_path") is string cp) s.ConfigPath = cp;
        if (Get(game, "patch_path", "patch_folder") is string pp) s.PatchPath = pp;
        if (Get(game, "language", "language") is string lg) s.Language = lg;

        if (Get(proton, "proton_path", "proton_path") is string pt) s.ProtonPath = pt;
        if (Get(proton, "prefix_path", "prefix_path") is string px) s.PrefixPath = px;
        s.EnableDxvk = GetBool(proton, "enable_dxvk", "enable_dxvk", s.EnableDxvk);
        if (Get(proton, "extra_wine_dll_overrides", "extra_wine_dll_overrides") is string xw) s.ExtraWineDllOverrides = xw;

        s.DalamudEnabled = GetBool(dalamud, "enabled", "dalamud_enabled", s.DalamudEnabled);

        if (Get(login, "username", "username") is string un) s.Username = un;
        s.SteamAccount = GetBool(login, "steam_account", "steam_account", s.SteamAccount);
        s.FreeTrial = GetBool(login, "free_trial", "free_trial", s.FreeTrial);
        if (Get(login, "current_account", "current_account") is string ca) s.CurrentAccountId = ca;

        return s;
    }

    private void ApplyDefaults()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(this.Settings.ProtonPath))
            this.Settings.ProtonPath = Path.Combine(home, ".local", "share", "Steam",
                "compatibilitytools.d", "GE-Proton11-6");

        if (string.IsNullOrEmpty(this.Settings.PrefixPath))
            this.Settings.PrefixPath = Path.Combine(this.storage.Root.FullName, "proton_prefix");

        if (string.IsNullOrEmpty(this.Settings.GamePath))
            this.Settings.GamePath = Path.Combine(home, "Games", "final-fantasy-xiv");

        if (string.IsNullOrEmpty(this.Settings.PatchPath))
            this.Settings.PatchPath = this.storage.GetFolder("patches").FullName;

        if (string.IsNullOrEmpty(this.Settings.ConfigPath))
            this.Settings.ConfigPath = this.storage.GetFolder("ffxivConfig").FullName;
    }

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
