using Avalonia;
using System;
using System.IO;

namespace FluxNew;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { Console.WriteLine($"UnhandledException: {e.ExceptionObject}"); } catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try { Console.WriteLine($"UnobservedTaskException: {e.Exception.Message}"); } catch { }
        };
        // Defer Lua and WoW API initialization until after Avalonia platform
        // services are available (MainWindow will initialize Lua).

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
