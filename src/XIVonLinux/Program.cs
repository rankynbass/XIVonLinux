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
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var storage = Path.Combine(home, ".xivonlinux");
        Directory.CreateDirectory(storage);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(storage, "xivonlinux.log"))
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
