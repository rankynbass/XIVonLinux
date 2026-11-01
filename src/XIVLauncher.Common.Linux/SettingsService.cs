using System;
using System.IO;
using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using XIVLauncher.Common;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// User-configurable launcher settings, persisted as ~/.xivonlinux/settings.toml
/// via XIVLauncher.Common.Storage("xivonlinux") so config, patches and dalamud
/// data live separate from the legacy ~/.xlcore. Credentials are NOT persisted
/// to disk — Password/OTP are UI-only fields, cleared on save.
/// </summary>
public sealed class LaunchSettings
{
    public string ProtonPath { get; set; } = "";
    public string PrefixPath { get; set; } = "";   // WINEPREFIX root; prefix dir = {PrefixPath}/pfx
    public string GameLocation { get; set; } = "";
    public string PatchFolder { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";   // UI-only, never persisted
    public string OneTimePassword { get; set; } = ""; // UI-only, never persisted
    public bool SteamAccount { get; set; } = true;
    public bool FreeTrial { get; set; }
    public bool DalamudEnabled { get; set; } = true;
    public string Language { get; set; } = "English";   // game client language: Japanese/English/German/French
}

/// <summary>
/// Loads/saves LaunchSettings with Tomlyn. Toml keys are snake_case.
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
    }

    public LaunchSettings Settings { get; private set; }

    /// <summary>The ~/.xivonlinux root, created on first use.</summary>
    public DirectoryInfo Root => this.storage.Root;

    /// <summary>A subfolder of ~/.xivonlinux, created if missing (e.g. "patches").</summary>
    public DirectoryInfo GetFolder(string name) => this.storage.GetFolder(name);

    /// <summary>A file path in ~/.xivonlinux (e.g. "aria2.log").</summary>
    public FileInfo GetFile(string name) => this.storage.GetFile(name);

    public void Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"proton_path = {TomlString(this.Settings.ProtonPath)}");
        sb.AppendLine($"prefix_path = {TomlString(this.Settings.PrefixPath)}");
        sb.AppendLine($"game_location = {TomlString(this.Settings.GameLocation)}");
        sb.AppendLine($"patch_folder = {TomlString(this.Settings.PatchFolder)}");
        sb.AppendLine($"username = {TomlString(this.Settings.Username)}");
        sb.AppendLine($"steam_account = {this.Settings.SteamAccount.ToString().ToLowerInvariant()}");
        sb.AppendLine($"free_trial = {this.Settings.FreeTrial.ToString().ToLowerInvariant()}");
        sb.AppendLine($"dalamud_enabled = {this.Settings.DalamudEnabled.ToString().ToLowerInvariant()}");
        sb.AppendLine($"language = {TomlString(this.Settings.Language)}");

        var file = this.storage.GetFile("settings.toml");
        File.WriteAllText(file.FullName, sb.ToString());
    }

    private LaunchSettings Load()
    {
        var s = new LaunchSettings();
        var file = this.storage.GetFile("settings.toml");
        if (!File.Exists(file.FullName))
            return s;

        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file.FullName));
        // TomlTable indexer throws on missing keys (older settings files lack newer
        // keys like prefix_path), so look keys up via ContainsKey.
        string? Get(string key) => model.ContainsKey(key) ? model[key] as string : null;
        bool GetBool(string key) => model.ContainsKey(key) && model[key] is bool b ? b : false;

        if (Get("proton_path") is string pp) s.ProtonPath = pp;
        if (Get("prefix_path") is string px) s.PrefixPath = px;
        if (Get("game_location") is string gl) s.GameLocation = gl;
        if (Get("patch_folder") is string pf) s.PatchFolder = pf;
        if (Get("username") is string un) s.Username = un;
        s.SteamAccount = GetBool("steam_account");
        s.FreeTrial = GetBool("free_trial");
        s.DalamudEnabled = GetBool("dalamud_enabled");
        if (Get("language") is string lg) s.Language = lg;
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

        if (string.IsNullOrEmpty(this.Settings.GameLocation))
            this.Settings.GameLocation = Path.Combine(home, "Games", "final-fantasy-xiv");

        if (string.IsNullOrEmpty(this.Settings.PatchFolder))
            this.Settings.PatchFolder = this.storage.GetFolder("patches").FullName;
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
