using System;
using System.Net.Http;
using System.Net.Http.Json;
using Serilog;

namespace XIVonLinux.Net;

/// <summary>
///     Represents a response from Kamori's GetLauncherClientConfig endpoint
///     (ported from XIVLauncher.Core Net/LauncherClientConfig.cs).
///     cutOffBootver drives the killswitch: when the local boot version is newer
///     than the cutoff, the launcher refuses to log in.
///     App layer: plain values only, handed to the library at call sites.
/// </summary>
public readonly struct LauncherClientConfig
{
    private const string LAUNCHER_CONFIG_URL = "https://kamori.goats.dev/Launcher/GetLauncherClientConfig";
    private const string FRONTIER_FALLBACK = "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}";

    public string frontierUrl { get; init; }
    public string? cutOffBootver { get; init; }
    public uint flags { get; init; }

    /// <summary>The fallback frontier template used when the config fetch fails.</summary>
    public static string FallbackFrontierUrl => FRONTIER_FALLBACK;

    public static async Task<LauncherClientConfig> GetAsync(HttpClient client)
    {
        LauncherClientConfig config;
        try
        {
            config = await client.GetFromJsonAsync<LauncherClientConfig>(LAUNCHER_CONFIG_URL).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not obtain LauncherClientConfig");
            config = new LauncherClientConfig()
            {
                frontierUrl = FRONTIER_FALLBACK,
            };
        }

        // Test/QA hook: XIVONLINUX_KILLSWITCH_CUTOFF forces a killswitch cutoff, and
        // XIVONLINUX_KILLSWITCH_FLAGS forces the flags bitmask, so the login gate and the
        // GlobalDisableDalamud bit can be exercised when Kamori publishes nothing (developer use).
        var overrideCutOff = Environment.GetEnvironmentVariable("XIVONLINUX_KILLSWITCH_CUTOFF");
        var overrideFlags = Environment.GetEnvironmentVariable("XIVONLINUX_KILLSWITCH_FLAGS");
        if (!string.IsNullOrEmpty(overrideCutOff) || !string.IsNullOrEmpty(overrideFlags))
        {
            var flagsVal = config.flags;
            if (!string.IsNullOrEmpty(overrideFlags))
                uint.TryParse(overrideFlags, out flagsVal);
            config = new LauncherClientConfig()
            {
                frontierUrl = config.frontierUrl,
                cutOffBootver = string.IsNullOrEmpty(overrideCutOff) ? config.cutOffBootver : overrideCutOff,
                flags = flagsVal,
            };
            Log.Information("KILLSWITCH OVERRIDE: cutOffBootver={Cutoff} flags={Flags} (env hooks)",
                config.cutOffBootver, config.flags);
        }

        return config;
    }
}
