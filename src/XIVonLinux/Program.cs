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
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(home, ".xlcore", "xivonlinux.log"))
            .CreateLogger();

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
