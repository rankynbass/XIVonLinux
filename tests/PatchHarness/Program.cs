// Headless patch pipeline trigger: drives LoginService exactly like the Play flow,
// so the user-set download speed + connection limits can be verified in the real
// aria2 daemon log. Run with an intentionally OLD boot ver in the game dir to force
// pending boot patches. Pass "repair" as argv[1] to run the Repair flow instead:
// login with the base version report, then PatchVerifier over all game files.
using System;
using System.IO;
using System.Threading.Tasks;
using XIVonLinux;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch;
using XivGame = XIVLauncher.Common.Game.Launcher;

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
