// Headless patch pipeline trigger: drives LoginService exactly like the Play flow,
// so the user-set download speed + connection limits can be verified in the real
// aria2 daemon log. Run with an intentionally OLD boot ver in the game dir to force
// pending boot patches. Pass "repair" as argv[1] to run the Repair flow instead:
// login with the base version report, then PatchVerifier over all game files.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using XIVonLinux;
using XIVLauncher.Common;
using XIVLauncher.Common.Linux;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch;
using XivGame = XIVLauncher.Common.Game.Launcher;

// Args-field parser check: `parse "<KEY=VALUE ...>"` runs LaunchSettings.ParseArgs on the
// argument string and prints each pair, so tokenization/quote/validation can be verified.
if (args.Length > 0 && args[0] == "parse")
{
    var input = args.Length > 1 ? args[1] : "";
    try
    {
        var pairs = LaunchSettings.ParseArgs(input);
        foreach (var (k, v) in pairs)
            Console.WriteLine($"[{k}]=[{v}]");
        Console.WriteLine($"parsed {pairs.Count} pair(s)");
    }
    catch (FormatException ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
    }
    return;
}

// E2E env check: `envtest "<KEY=VALUE ...>"` parses the pairs, builds a UmuPrefix from the
// saved settings, and runs `cmd /c set` inside the prefix with those pairs as environment
// variables. Prints the captured in-prefix environment so we can see the pairs arrived.
if (args.Length > 0 && args[0] == "envtest")
{
    var s = new SettingsService();
    var input = args.Length > 1 ? args[1] : "";
    var pairs = LaunchSettings.ParseArgs(input);
    var env = new System.Collections.Generic.Dictionary<string, string>();
    foreach (var (k, v) in pairs)
        env[k] = v;

    var prefixRoot = s.Settings.PrefixPath;
    var prefix = new UmuPrefix(
        new DirectoryInfo(Path.Combine(prefixRoot, "pfx")),
        prefixRoot,
        s.Settings.GamePath,
        "umu-steam",
        s.Settings.ProtonPath,
        null, null);
    var umuRun = $"/home/rankyn/.local/share/xivonlinux/tools/umu/1.4.4/umu-run";
    var runner = new UmuCommandRunner(prefix, umuRun, null, s.Settings.WineDebugEnv);
    Console.WriteLine($"prefix: {prefixRoot}  umu: {umuRun}  pairs: {pairs.Count}");

    var proc = runner.RunInPrefix(new[] { "cmd", "/c", "set" }, "", env, true, false);
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    foreach (var (k, v) in pairs)
    {
        var found = stdout.Split('\n').Any(line => line.Trim() == $"{k}={v}");
        Console.WriteLine(found ? $"OK   {k}={v} seen in prefix env"
                                : $"MISS {k}={v} NOT in prefix env");
    }
    return;
}
if (args.Length > 0 && args[0] == "settings")
{
    var s = new SettingsService();
    Console.WriteLine($"wine_debug_enabled read back as: {s.Settings.WineDebugEnabled}");
    Console.WriteLine($"wine_debug_value: {s.Settings.WineDebugValue}");
    Console.WriteLine($"WineDebugEnv resolves to: {s.Settings.WineDebugEnv}");
    Console.WriteLine($"hacks: fix_ldp={s.Settings.FixLdp} fix_xmodifiers={s.Settings.FixXModifiers} " +
        $"fix_libicuuc={s.Settings.FixLibIcuuc} fix_locale={s.Settings.FixLocale} dont_use_system_tz={s.Settings.DontUseSystemTz}");
    Console.WriteLine($"hack env pairs: {string.Join(" ", s.Settings.HackEnvPairs().Select(p => $"{p.Key}={p.Value}"))}");
    return;
}
if (args.Length > 0 && args[0] == "hacks")
{
    var s = new SettingsService();
    s.Settings.FixLdp = true; s.Settings.FixXModifiers = true; s.Settings.FixLibIcuuc = true;
    s.Settings.FixLocale = true; s.Settings.DontUseSystemTz = true;
    Environment.SetEnvironmentVariable("LD_PRELOAD", "/usr/lib/gameoverlayrenderer.so:/usr/lib/libnss_cache.so.2");
    var pairs = s.Settings.HackEnvPairs().ToList();
    var env = new System.Collections.Generic.Dictionary<string, string>();
    foreach (var p in pairs) env[p.Key] = p.Value;

    var prefixRoot = s.Settings.PrefixPath;
    var prefix = new UmuPrefix(
        new DirectoryInfo(Path.Combine(prefixRoot, "pfx")),
        prefixRoot,
        s.Settings.GamePath,
        "umu-steam",
        s.Settings.ProtonPath,
        null, null);
    var umuRun = $"/home/rankyn/.local/share/xivonlinux/tools/umu/1.4.4/umu-run";
    var runner = new UmuCommandRunner(prefix, umuRun, null, s.Settings.WineDebugEnv);

    var proc = runner.RunInPrefix(new[] { "cmd", "/c", "set" }, "", env, true, false);
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();

    var lines = stdout.Split('\n').Select(l => l.Trim());
    foreach (var l in lines.Where(l => l.StartsWith("LD_PRELOAD=") || l.StartsWith("LC_")))
        Console.WriteLine($"prefix env: {l}");
    void Check(string expected)
        => Console.WriteLine(lines.Contains(expected) ? $"OK   {expected}" : $"MISS {expected}");

    Check("XMODIFIERS=@im=null");
    Check("DOTNET_SYSTEM_GLOBALIZATION_USENLS=true");
    var ctype = LaunchSettings.DetectCType();
    Console.WriteLine($"detected ctype: '{ctype}'");
    Check($"LC_CTYPE={ctype}");
    // LD_PRELOAD: gameoverlayrenderer must be gone, the rest survives (pressure-vessel
    // remaps host paths into /run/host). LC_ALL is consumed by wine's setlocale init
    // and is not in the Windows env block, so it is not checked here.
    var ldp = lines.FirstOrDefault(l => l.StartsWith("LD_PRELOAD="));
    Console.WriteLine(ldp is not null && !ldp.Contains("gameoverlayrenderer") && ldp.Contains("libnss_cache.so.2")
        ? "OK   LD_PRELOAD cleaned" : $"MISS LD_PRELOAD cleaned ({ldp})");
    Check("TZ=");
    return;
}
var settings = new SettingsService();
XIVonLinux.Program.InstallAria2Shim();   // PATH shim + XIVON_* env, same as app startup
Console.WriteLine($"PATH head: {(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')[0]}");
Console.WriteLine($"XIVON_MAX_CONNECTIONS={Environment.GetEnvironmentVariable("XIVON_MAX_CONNECTIONS")} XIVON_ARIA2_REAL={Environment.GetEnvironmentVariable("XIVON_ARIA2_REAL")}");
var accountManager = new AccountManager(settings);
var account = accountManager.ByUserName("rankynfree");
if (account is null)
{
    Console.WriteLine("account rankynfree not found");
    return;
}

Console.WriteLine($"speed={settings.Settings.MaxDownloadSpeedKibps}KiB/s connections={settings.Settings.MaxConnections} game={settings.Settings.GamePath}");

var service = new LoginService(settings.Storage, settings.Settings.ConfigPath,
    settings.GetCacheFolder("patches").FullName, null!, ClientLanguage.English,
    "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}",
    settings.Settings.MaxDownloadSpeedKibps);

var gamePath = new DirectoryInfo(settings.Settings.GamePath);

if (args.Length > 0 && args[0] == "repair")
{
    var login = await service.LoginAsync(new LoginRequest
    {
        Username = account.UserName,
        Password = account.Password,
        OneTimePassword = "",
        SteamAccount = account.UseSteamServiceAccount,
        FreeTrial = account.IsFreeTrial,
    }, gamePath, forceBaseVersion: true);
    Console.WriteLine($"repair login state: {login.State}, patch sets: {login.PendingPatches.Length}");
    if (login.State == XivGame.LoginState.NeedsPatchGame)
    {
        var outcome = await service.RepairAsync(login, settings.Settings.GamePath, Console.WriteLine);
        Console.WriteLine($"repair outcome: {outcome.repaired} repaired, {outcome.moved} moved");
    }
    Console.WriteLine("harness done");
    return;
}

var bootPatches = await service.CheckBootPatchesAsync(gamePath);
Console.WriteLine($"boot patch entries: {bootPatches.Length}");

if (bootPatches.Length > 0)
    await service.InstallPatchesAsync(Repository.Boot, bootPatches, string.Empty,
        settings.Settings.GamePath, settings.Settings.PatchPath, Console.WriteLine);

Console.WriteLine("harness done");
