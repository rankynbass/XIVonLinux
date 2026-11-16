using System;
using System.IO;
using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Logging;
using Serilog;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;
using XIVLauncher.Common.Http.HappyEyeballs;

namespace XIVonLinux;

public static class Program
{
    /// <summary>The app-wide HttpClient: HappyEyeballs ConnectCallback (XIVLauncher.Core
    /// parity — IPv6/IPv4 connect preference), auto-decompression, redirects, and the app
    /// User-Agent generated from AppUtil. All XIVonLinux HTTP uses this, and it gets
    /// passed to XIVLauncher.Common.Linux components (PatchVerifier, DalamudUpdater, ...)
    /// that need a client.</summary>
    public static HttpClient HttpClient { get; } = CreateAppHttpClient();

    private static HttpClient CreateAppHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = new HappyEyeballsCallback().ConnectCallback,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 4,
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"XIVonLinux/{AppUtil.GetAssemblyVersion()} (FFXIV launcher; https://github.com/rankynbass/XIVonLinux)");
        return client;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // XDG layout: one-time move of the pre-XDG ~/.xivonlinux tree into
        // ~/.config/~/.cache/~/.local/share under xivonlinux; must run before any
        // storage root is opened so everything lands in the XDG dirs.
        XdgStorage.MigrateFromLegacy();
        InstallAria2Shim();
        var logsDir = Path.Combine(XdgStorage.DataRoot, "logs");
        Directory.CreateDirectory(logsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(logsDir, "xivonlinux.log"))
            .CreateLogger();

        var versionInfo = XIVonLinux.AppUtil.VersionInfo.Instance();
        Log.Information("XIVonLinux {Name} v{Version}", versionInfo.Version.FileName, versionInfo.Version.FileVersion);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// lib/FFXIVQuickLauncher starts the aria2 daemon with a hardcoded
    /// --max-connection-per-server=8 (submodule, not editable). That startup arg is
    /// the effective limit — aria2 ignores max-connection-per-server as a per-download
    /// option, and RPC changeGlobalOption cannot change it. This shim sits in front of
    /// the real aria2c on PATH and rewrites that one arg to the user setting, leaving
    /// lib untouched.
    /// </summary>
    public static void InstallAria2Shim()
    {
        var shimDir = Path.Combine(XdgStorage.CacheRoot, "aria2_shim");
        var real = FindInPath("aria2c");
        if (real is null)
            return;

        Directory.CreateDirectory(shimDir);
        var script = Path.Combine(shimDir, "aria2c");
        File.WriteAllText(script,
            "#!/usr/bin/env bash\n" +
            "real=\"$XIVON_ARIA2_REAL\"\n" +
            "n=\"${XIVON_MAX_CONNECTIONS:-4}\"\n" +
            "out=()\n" +
            "for a in \"$@\"; do\n" +
            "  case \"$a\" in\n" +
            "    --max-connection-per-server=*) out+=(\"--max-connection-per-server=$n\") ;;\n" +
            "    *) out+=(\"$a\") ;;\n" +
            "  esac\n" +
            "done\n" +
            "exec \"$real\" \"${out[@]}\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // The value comes from settings.toml [patch] max_connections; the shim
        // reads XIVON_MAX_CONNECTIONS when the daemon starts.
        var maxConnections = 4;
        var settingsPath = Path.Combine(XdgStorage.ConfigRoot, "settings.toml");
        if (File.Exists(settingsPath))
        {
            var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(settingsPath));
            if (model.ContainsKey("patch"))
            {
                var section = model["patch"];
                if (section is TomlTable patch && patch.ContainsKey("max_connections"))
                {
                    var value = patch["max_connections"];
                    if (value is long conn)
                        maxConnections = Math.Clamp((int)conn, 1, 16);
                }
            }
        }

        Environment.SetEnvironmentVariable("XIVON_ARIA2_REAL", real);
        Environment.SetEnvironmentVariable("XIVON_MAX_CONNECTIONS", maxConnections.ToString());
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", string.IsNullOrEmpty(path) ? shimDir : shimDir + ":" + path);
    }

    private static string? FindInPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Debug);
}
