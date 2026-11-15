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
    public string AdditionalArgs { get; set; } = "";  // extra key=value game args (XIVLauncher.Core AdditionalArgs)
    public string DpiAwareness { get; set; } = "Unaware"; // DpiAwareness name: Aware/Unaware
    public bool IgnoreSteam { get; set; }             // never use Steam service-account flags at launch
    public bool UseXlAuthMacros { get; set; }         // XL auth macros (IsOtpServer parity; no server on Linux, UI parity)
    public bool UseUidCache { get; set; }             // enable the uid cache (XIVLauncher.Core cache is a stub; parity UI)
    public string UidCache { get; set; } = "";        // cached uid string; Reset button clears it

    // Proton tab
    public string ProtonPath { get; set; } = "";
    public string PrefixPath { get; set; } = "";      // WINEPREFIX root; prefix dir = {PrefixPath}/pfx
    public bool ManagedProton { get; set; }           // false = custom folder pick, true = pick from enumerated Proton list
    public bool ManagedUmu { get; set; } = true;      // false = custom umu-run folder pick, true = pick latest/previous managed zipapp
    public string UmuTag { get; set; } = "";          // managed mode: selected tools/umu/<tag>
    public string UmuPath { get; set; } = "";         // custom mode: folder on disk containing umu-run
    public bool EnableDxvk { get; set; } = true;       // DXVK preset for WINEDLLOVERRIDES
    public string ExtraWineDllOverrides { get; set; } = ""; // appended after the DXVK preset
    public bool WineDebugEnabled { get; set; } = true; // false forces WINEDEBUG=-all
    public string WineDebugValue { get; set; } = DefaultWineDebugValue; // classes when enabled; blank restores default

    /// <summary>Wine debug classes for in-prefix launches: -all when WINEDEBUG is
    /// disabled, the configured value when enabled; a blank value restores the default.</summary>
    public string WineDebugEnv => !WineDebugEnabled ? "-all"
        : string.IsNullOrWhiteSpace(WineDebugValue) ? DefaultWineDebugValue : WineDebugValue;

    public const string DefaultWineDebugValue = "+warn,+overlay,fixme-all";

    // Dalamud tab
    public bool DalamudEnabled { get; set; } = true;
    public string LoadMethod { get; set; } = "EntryPoint"; // DalamudLoadMethod name: EntryPoint/DllInject/ACLonly
    public string BranchTrack { get; set; } = "";     // Kamori branch track; empty = release
    public string BranchKey { get; set; } = "";       // Kamori branch key, fetched with the track
    public int LoadDelay { get; set; } = 0;           // injection delay in ms (--dalamud-delay-initialize)
    public bool ManualInjectEnabled { get; set; }     // use a local injector build instead of the updater's
    public string ManualInjectPath { get; set; } = "";

    // Login (not a tab; in-memory session state prefilled from accounts.db — settings.toml
    // persists only current_account, per Alex: identity comes from the database)
    public string Username { get; set; } = "";        // UI-only, from the current account
    public string Password { get; set; } = "";        // UI-only, never persisted
    public string OneTimePassword { get; set; } = ""; // UI-only, never persisted
    public bool SteamAccount { get; set; } = true;    // UI-only, from the current account
    public bool FreeTrial { get; set; }               // UI-only, from the current account
    public string? CurrentAccountId { get; set; }     // accounts.db user_name the launcher pre-selects
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
    private readonly Storage configStorage;
    private readonly Storage cacheStorage;

    public SettingsService()
    {
        // XDG layout (XdgStorage): data root is the main storage, settings-era
        // files ride in config, patches/temp downloads in cache.
        this.storage = new Storage("xivonlinux", XdgStorage.DataRoot);
        this.configStorage = new Storage("xivonlinux", XdgStorage.ConfigRoot);
        this.cacheStorage = new Storage("xivonlinux", XdgStorage.CacheRoot);
        this.Settings = Load();
        ApplyDefaults();
        // Fresh copy on disk after missing/malformed/quarantined settings (and any
        // newly defaulted entries); a no-op write when the file is already complete.
        Save();
    }

    public LaunchSettings Settings { get; private set; }

    /// <summary>The data root (~/.local/share/xivonlinux), created on first use.</summary>
    public DirectoryInfo Root => this.storage.Root;

    /// <summary>The XIVLauncher.Common storage; passed to library services as values
    /// (GetFile/GetFolder), not as the settings object.</summary>
    public Storage Storage => this.storage;

    /// <summary>A subfolder of the data root, created if missing (prefixes, tools, logs).</summary>
    public DirectoryInfo GetFolder(string name) => this.storage.GetFolder(name);

    /// <summary>A file path in the data root (in-prefix logs, wine.log).</summary>
    public FileInfo GetFile(string name) => this.storage.GetFile(name);

    /// <summary>A file in ~/.config/xivonlinux (settings.toml, accounts.db, accounts.deck).</summary>
    public FileInfo GetConfigFile(string name) => this.configStorage.GetFile(name);

    /// <summary>A subfolder of ~/.cache/xivonlinux, created if missing (patches).</summary>
    public DirectoryInfo GetCacheFolder(string name) => this.cacheStorage.GetFolder(name);

    public void Save()
    {
        var builder = new StringBuilder();

        builder.AppendLine("[game]");
        builder.AppendLine($"game_path = {TomlString(this.Settings.GamePath)}");
        builder.AppendLine($"config_path = {TomlString(this.Settings.ConfigPath)}");
        builder.AppendLine($"patch_path = {TomlString(this.Settings.PatchPath)}");
        builder.AppendLine($"language = {TomlString(this.Settings.Language)}");
        builder.AppendLine($"additional_args = {TomlString(this.Settings.AdditionalArgs)}");
        builder.AppendLine($"dpi_awareness = {TomlString(this.Settings.DpiAwareness)}");
        builder.AppendLine($"ignore_steam = {this.Settings.IgnoreSteam.ToString().ToLowerInvariant()}");
        builder.AppendLine($"uid_cache = {TomlString(this.Settings.UidCache)}");
        builder.AppendLine($"use_uid_cache = {this.Settings.UseUidCache.ToString().ToLowerInvariant()}");
        builder.AppendLine($"use_xl_auth_macros = {this.Settings.UseXlAuthMacros.ToString().ToLowerInvariant()}");
        builder.AppendLine();

        builder.AppendLine("[proton]");
        builder.AppendLine($"proton_path = {TomlString(this.Settings.ProtonPath)}");
        builder.AppendLine($"prefix_path = {TomlString(this.Settings.PrefixPath)}");
        builder.AppendLine($"managed_proton = {this.Settings.ManagedProton.ToString().ToLowerInvariant()}");
        builder.AppendLine($"managed_umu = {this.Settings.ManagedUmu.ToString().ToLowerInvariant()}");
        builder.AppendLine($"umu_tag = {TomlString(this.Settings.UmuTag)}");
        builder.AppendLine($"umu_path = {TomlString(this.Settings.UmuPath)}");
        builder.AppendLine($"enable_dxvk = {this.Settings.EnableDxvk.ToString().ToLowerInvariant()}");
        builder.AppendLine($"extra_wine_dll_overrides = {TomlString(this.Settings.ExtraWineDllOverrides)}");
        builder.AppendLine($"wine_debug_enabled = {this.Settings.WineDebugEnabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"wine_debug_value = {TomlString(this.Settings.WineDebugValue)}");
        builder.AppendLine();

        builder.AppendLine("[dalamud]");
        builder.AppendLine($"branch_key = {TomlString(this.Settings.BranchKey)}");
        builder.AppendLine($"branch_track = {TomlString(this.Settings.BranchTrack)}");
        builder.AppendLine($"enabled = {this.Settings.DalamudEnabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"load_delay = {this.Settings.LoadDelay}");
        builder.AppendLine($"load_method = {TomlString(this.Settings.LoadMethod)}");
        builder.AppendLine($"manual_inject_enabled = {this.Settings.ManualInjectEnabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"manual_inject_path = {TomlString(this.Settings.ManualInjectPath)}");
        builder.AppendLine();

        builder.AppendLine("[login]");
        // Login identity comes from accounts.db: [login] persists only the current account.
        if (!string.IsNullOrEmpty(this.Settings.CurrentAccountId))
            builder.AppendLine($"current_account = {TomlString(this.Settings.CurrentAccountId)}");

        var file = this.configStorage.GetFile("settings.toml");
        File.WriteAllText(file.FullName, builder.ToString());
    }

    private LaunchSettings Load()
    {
        var settings = new LaunchSettings();
        var file = this.configStorage.GetFile("settings.toml");
        if (!File.Exists(file.FullName))
            return settings;

        TomlTable model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(file.FullName));
        }
        catch (Exception exception)
        {
            // Malformed TOML must not crash the launcher: quarantine the file as
            // settings.toml.bad (numbered on collision) and start from defaults;
            // ApplyDefaults + the startup Save() write a fresh settings.toml.
            Log.Error(exception, "settings.toml is malformed; quarantining as settings.toml.bad");
            var badPath = Path.Combine(file.DirectoryName!, "settings.toml.bad");
            var collisionNumber = 1;
            while (File.Exists(badPath))
                badPath = Path.Combine(file.DirectoryName!, $"settings.toml.bad.{collisionNumber++}");
            File.Move(file.FullName, badPath);
            return settings;
        }

        // Sectioned keys; TomlTable indexer throws on missing keys, so look keys up
        // via ContainsKey. A key present with the wrong type, and a missing section,
        // fall back to the legacy flat top-level key, then to the property default —
        // pre-section settings.toml files migrate on the next Save().
        TomlTable? Sec(string name) => model.ContainsKey(name) && model[name] is TomlTable sectionTable ? sectionTable : null;

        string? Get(TomlTable? sec, string key, string legacyKey)
        {
            if (sec is not null && sec.ContainsKey(key) && sec[key] is string found)
                return found;
            if (model.ContainsKey(legacyKey) && model[legacyKey] is string legacyValue)
                return legacyValue;
            return null;
        }

        bool GetBool(TomlTable? sec, string key, string legacyKey, bool def)
        {
            if (sec is not null && sec.ContainsKey(key) && sec[key] is bool foundFlag)
                return foundFlag;
            if (model.ContainsKey(legacyKey) && model[legacyKey] is bool legacyFlag)
                return legacyFlag;
            return def;
        }

        int GetInt(TomlTable? sec, string key, int def)
        {
            if (sec is not null && sec.ContainsKey(key) && sec[key] is int num)
                return num;
            return def;
        }

        var game = Sec("game");
        var proton = Sec("proton");
        var dalamud = Sec("dalamud");
        var login = Sec("login");

        if (Get(game, "game_path", "game_location") is string gamePath) settings.GamePath = gamePath;
        if (Get(game, "config_path", "config_path") is string configPath) settings.ConfigPath = configPath;
        if (Get(game, "patch_path", "patch_folder") is string patchPath) settings.PatchPath = patchPath;
        if (Get(game, "language", "language") is string language) settings.Language = language;
        if (Get(game, "additional_args", "additional_args") is string additionalArgs) settings.AdditionalArgs = additionalArgs;
        if (Get(game, "dpi_awareness", "dpi_awareness") is string dpi) settings.DpiAwareness = dpi;
        settings.IgnoreSteam = GetBool(game, "ignore_steam", "ignore_steam", settings.IgnoreSteam);
        if (Get(game, "uid_cache", "uid_cache") is string uidCache) settings.UidCache = uidCache;
        settings.UseUidCache = GetBool(game, "use_uid_cache", "use_uid_cache", settings.UseUidCache);
        settings.UseXlAuthMacros = GetBool(game, "use_xl_auth_macros", "use_xl_auth_macros", settings.UseXlAuthMacros);

        if (Get(proton, "proton_path", "proton_path") is string protonPath) settings.ProtonPath = protonPath;
        if (Get(proton, "prefix_path", "prefix_path") is string prefixPath) settings.PrefixPath = prefixPath;
        settings.ManagedProton = GetBool(proton, "managed_proton", "managed_proton", settings.ManagedProton);
        settings.ManagedUmu = GetBool(proton, "managed_umu", "managed_umu", settings.ManagedUmu);
        if (Get(proton, "umu_tag", "umu_tag") is string umuTag) settings.UmuTag = umuTag;
        if (Get(proton, "umu_path", "umu_path") is string umuPath) settings.UmuPath = umuPath;
        settings.EnableDxvk = GetBool(proton, "enable_dxvk", "enable_dxvk", settings.EnableDxvk);
        if (Get(proton, "extra_wine_dll_overrides", "extra_wine_dll_overrides") is string wineDllOverrides) settings.ExtraWineDllOverrides = wineDllOverrides;
        if (Get(proton, "wine_debug_enabled", "wine_debug_enabled") is string wde) settings.WineDebugEnabled = wde == "true";
        if (Get(proton, "wine_debug_value", "wine_debug_value") is string wdv)
            settings.WineDebugValue = string.IsNullOrWhiteSpace(wdv) ? LaunchSettings.DefaultWineDebugValue : wdv;

        settings.DalamudEnabled = GetBool(dalamud, "enabled", "dalamud_enabled", settings.DalamudEnabled);
        if (Get(dalamud, "branch_key", "branch_key") is string branchKey) settings.BranchKey = branchKey;
        if (Get(dalamud, "branch_track", "branch_track") is string branchTrack) settings.BranchTrack = branchTrack;
        settings.LoadDelay = GetInt(dalamud, "load_delay", settings.LoadDelay);
        if (Get(dalamud, "load_method", "load_method") is string loadMethod) settings.LoadMethod = loadMethod;
        settings.ManualInjectEnabled = GetBool(dalamud, "manual_inject_enabled", "manual_inject_enabled", settings.ManualInjectEnabled);
        if (Get(dalamud, "manual_inject_path", "manual_inject_path") is string manualInjectPath) settings.ManualInjectPath = manualInjectPath;

        if (Get(login, "current_account", "current_account") is string currentAccountId) settings.CurrentAccountId = currentAccountId;

        return settings;
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
            this.Settings.PatchPath = this.cacheStorage.GetFolder("patches").FullName;

        if (string.IsNullOrEmpty(this.Settings.ConfigPath))
            this.Settings.ConfigPath = this.storage.GetFolder("ffxivConfig").FullName;
    }

    private static string TomlString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(c); break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }
}
