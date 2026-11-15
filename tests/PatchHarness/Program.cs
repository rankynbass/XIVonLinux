// Headless patch pipeline trigger: drives LoginService.CheckBootPatchesAsync +
// InstallPatchesAsync exactly like the Play flow, so the user-set download
// speed + connection limits can be verified in the real aria2 daemon log.
// Run with an intentionally OLD boot ver in the game dir to force pending boot patches.
using System;
using System.IO;
using System.Threading.Tasks;
using XIVonLinux;
using XIVLauncher.Common;
using XIVLauncher.Common.Game.Patch;

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

Console.WriteLine($"speed={settings.Settings.MaxDownloadSpeedMbps}Mbps connections={settings.Settings.MaxConnections} game={settings.Settings.GamePath}");

var service = new LoginService(settings.Storage, settings.Settings.ConfigPath,
    settings.GetCacheFolder("patches").FullName, null!, ClientLanguage.English,
    "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}",
    settings.Settings.MaxDownloadSpeedMbps);

var gamePath = new DirectoryInfo(settings.Settings.GamePath);
var bootPatches = await service.CheckBootPatchesAsync(gamePath);
Console.WriteLine($"boot patch entries: {bootPatches.Length}");

if (bootPatches.Length > 0)
    await service.InstallPatchesAsync(Repository.Boot, bootPatches, string.Empty,
        settings.Settings.GamePath, settings.Settings.PatchPath, Console.WriteLine);

Console.WriteLine("harness done");
