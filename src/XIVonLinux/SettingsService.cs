using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Serilog;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using XIVLauncher.Common;

namespace XIVonLinux;

/// <summary>
/// User-configurable launcher settings, persisted as settings.toml in
/// ~/.config/xivonlinux via XIVLauncher.Common.Storage("xivonlinux"), split into
/// [game], [proton], [dalamud], [login] and [patch] sections. Credentials are NOT
/// persisted to disk — Password/OTP are UI-only fields, cleared on save.
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

    // Patching tab
    public int MaxDownloadSpeedKibps { get; set; } = 0; // KiB/s, 0 = unlimited; aria2 max-overall-download-limit
    public int MaxConnections { get; set; } = 4;       // aria2 max-connection-per-server (1-16)

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

    /// <summary>Troubleshooting hacks (XIVLauncher.Core parity): environment changes applied
    /// to every game launch. All default off; each produces env pairs via HackEnvPairs.</summary>
    public bool FixLdp { get; set; }          // strip gameoverlayrenderer.so entries from LD_PRELOAD
    public bool FixXModifiers { get; set; }   // XMODIFIERS=@im=null
    public bool FixLibIcuuc { get; set; }     // DOTNET_SYSTEM_GLOBALIZATION_USENLS=true
    public bool FixLocale { get; set; }       // LC_ALL/LC_CTYPE = detected C.* UTF-8 locale
    public bool DontUseSystemTz { get; set; } // TZ set to empty string

    /// <summary>First C.* UTF-8 locale found via locale -a (XIVLauncher.Core GetCType shape);
    /// empty string when none exists. Cached, since launches compute it repeatedly.</summary>
    public static string DetectCType()
    {
        if (cachedCType is not null)
            return cachedCType;
        var psi = new ProcessStartInfo("sh")
        {
            Arguments = "-c \"locale -a 2>/dev/null | grep -i utf\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        var lines = proc.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        cachedCType = Array.Find(lines, s => s.ToUpper().StartsWith("C.")) ?? string.Empty;
        return cachedCType;
    }

    private static string? cachedCType;

    /// <summary>Environment pairs produced by the enabled hacks. Plain types only (layering
    /// rule); merge at EVERY launch site — pairs from AdditionalArgs concatenate after so the
    /// user can override a hack, exactly as XIVLauncher.Core's TZ comment intends.</summary>
    public IEnumerable<KeyValuePair<string, string>> HackEnvPairs()
    {
        var pairs = new List<KeyValuePair<string, string>>();

        if (FixLdp)
        {
            var dirty = Environment.GetEnvironmentVariable("LD_PRELOAD") ?? string.Empty;
            var kept = dirty.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => !s.Contains("gameoverlayrenderer.so"));
            pairs.Add(new KeyValuePair<string, string>("LD_PRELOAD", string.Join(":", kept)));
        }

        if (FixLocale)
        {
            var ctype = DetectCType();
            if (ctype.Length > 0)
            {
                pairs.Add(new KeyValuePair<string, string>("LC_ALL", ctype));
                pairs.Add(new KeyValuePair<string, string>("LC_CTYPE", ctype));
            }
        }

        if (FixLibIcuuc)
            pairs.Add(new KeyValuePair<string, string>("DOTNET_SYSTEM_GLOBALIZATION_USENLS", "true"));

        if (FixXModifiers)
            pairs.Add(new KeyValuePair<string, string>("XMODIFIERS", "@im=null"));

        if (DontUseSystemTz)
            pairs.Add(new KeyValuePair<string, string>("TZ", string.Empty));

        return pairs;
    }

    /// <summary>Parse AdditionalArgs as a space-separated list of KEY=VALUE pairs. VALUE
    /// may be double-quoted, single-quoted, or unquoted; KEY= (empty value) is valid,
    /// treated as "". Spaces are allowed only between pairs and inside quotes. Returns
    /// the pairs in order. Throws FormatException naming the offending key when a token
    /// has no '=' (a bare KEY), an unterminated quote, trailing text after a quoted
    /// value, an empty key, or a key that is not a valid environment-variable name.</summary>
    public static List<(string key, string value)> ParseArgs(string args)
    {
        var pairs = new List<(string, string)>();
        var i = 0;
        var n = args.Length;
        while (i < n)
        {
            // Skip whitespace between pairs.
            while (i < n && char.IsWhiteSpace(args[i])) i++;
            if (i >= n) break;

            // Key runs up to the first '=' (spaces are never part of a key).
            var keyStart = i;
            while (i < n && args[i] != '=' && !char.IsWhiteSpace(args[i])) i++;
            var key = args.Substring(keyStart, i - keyStart);
            if (i >= n || args[i] != '=')
                throw new FormatException($"'{key}' is missing a value — use KEY=VALUE (KEY= means empty).");
            i++;   // consume '='

            string value;
            if (i < n && (args[i] == '"' || args[i] == '\''))
            {
                var quote = args[i];
                i++;   // consume opening quote
                var valueStart = i;
                while (i < n && args[i] != quote) i++;   // spaces inside quotes are kept
                if (i >= n)
                    throw new FormatException($"'{key}' has an unterminated quote in its value.");
                value = args.Substring(valueStart, i - valueStart);
                i++;   // consume closing quote
                // Spaces belong between pairs, so nothing non-space may follow the closing quote.
                if (i < n && !char.IsWhiteSpace(args[i]))
                    throw new FormatException($"'{key}' has extra text after its quoted value — spaces are only allowed between KEY=VALUE pairs.");
            }
            else
            {
                var valueStart = i;
                while (i < n && !char.IsWhiteSpace(args[i])) i++;   // unquoted value: no spaces
                value = args.Substring(valueStart, i - valueStart);
            }

            if (key.Length == 0)
                throw new FormatException("a KEY=VALUE pair is missing its key.");
            if (!IsValidEnvKey(key))
                throw new FormatException($"'{key}' is not a valid environment variable name (letters, digits, underscore; not starting with a digit).");
            pairs.Add((key, value));
        }
        return pairs;
    }

    /// <summary>Environment-variable name rule: first char letter/underscore, all chars
    /// letter/digit/underscore.</summary>
    private static bool IsValidEnvKey(string key)
    {
        if (key.Length == 0 || !(char.IsLetter(key[0]) || key[0] == '_'))
            return false;
        foreach (var c in key)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

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

    /// <summary>Reset Settings (used by the Troubleshooting tab's Reset Settings button):
    /// fresh LaunchSettings + ApplyDefaults + a fresh settings.toml write. Callers delete
    /// accounts.db/accounts.deck and rebuild accounts separately.</summary>
    public void ResetToDefaults()
    {
        this.Settings = new LaunchSettings();
        ApplyDefaults();
        Save();
    }

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
        builder.AppendLine();

        builder.AppendLine("[patch]");
        builder.AppendLine($"max_download_speed_kibps = {this.Settings.MaxDownloadSpeedKibps}");
        builder.AppendLine($"max_connections = {this.Settings.MaxConnections}");
        builder.AppendLine();

        builder.AppendLine("[hack]");
        builder.AppendLine($"fix_ldp = {this.Settings.FixLdp.ToString().ToLowerInvariant()}");
        builder.AppendLine($"fix_xmodifiers = {this.Settings.FixXModifiers.ToString().ToLowerInvariant()}");
        builder.AppendLine($"fix_libicuuc = {this.Settings.FixLibIcuuc.ToString().ToLowerInvariant()}");
        builder.AppendLine($"fix_locale = {this.Settings.FixLocale.ToString().ToLowerInvariant()}");
        builder.AppendLine($"dont_use_system_tz = {this.Settings.DontUseSystemTz.ToString().ToLowerInvariant()}");

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
            if (sec is not null && sec.ContainsKey(key))
            {
                // TOML integers deserialize as Int64, not Int32
                if (sec[key] is int num)
                    return num;
                if (sec[key] is long wide)
                    return (int)wide;
            }
            return def;
        }

        var game = Sec("game");
        var proton = Sec("proton");
        var dalamud = Sec("dalamud");
        var login = Sec("login");
        var patch = Sec("patch");

        settings.MaxDownloadSpeedKibps = Math.Clamp(GetInt(patch, "max_download_speed_kibps", settings.MaxDownloadSpeedKibps), 0, 10485760);
        settings.MaxConnections = Math.Clamp(GetInt(patch, "max_connections", settings.MaxConnections), 1, 16);

        var hack = Sec("hack");
        settings.FixLdp = GetBool(hack, "fix_ldp", "fix_ldp", settings.FixLdp);
        settings.FixXModifiers = GetBool(hack, "fix_xmodifiers", "fix_xmodifiers", settings.FixXModifiers);
        settings.FixLibIcuuc = GetBool(hack, "fix_libicuuc", "fix_libicuuc", settings.FixLibIcuuc);
        settings.FixLocale = GetBool(hack, "fix_locale", "fix_locale", settings.FixLocale);
        settings.DontUseSystemTz = GetBool(hack, "dont_use_system_tz", "dont_use_system_tz", settings.DontUseSystemTz);

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
        settings.WineDebugEnabled = GetBool(proton, "wine_debug_enabled", "wine_debug_enabled", settings.WineDebugEnabled);
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
