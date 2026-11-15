using System;
using System.IO;
using Avalonia;
using Avalonia.Logging;
using Serilog;

namespace XIVonLinux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // XDG layout: one-time move of the pre-XDG ~/.xivonlinux tree into
        // ~/.config/~/.cache/~/.local/share under xivonlinux; must run before any
        // storage root is opened so everything lands in the XDG dirs.
        XdgStorage.MigrateFromLegacy();
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Debug);
}
