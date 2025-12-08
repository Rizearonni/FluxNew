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
        // Startup smoke test: run a simple Lua expression and print the result.
        try
        {
            var (ok, result) = KopiLuaRunner.TryRun("return 1+1");
            Console.WriteLine($"KopiLuaRunner.TryRun: success={ok}, result={result}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"KopiLuaRunner smoke test failed: {ex}");
        }

        // Install minimal WoW API shim into the Lua state.
        try
        {
            WoWApi.LoadApi();

            // Attempt to load a sample addon if present in a local `addons` folder.
            var samplePath = Path.Combine(Environment.CurrentDirectory, "addons", "SampleAddon");
            if (Directory.Exists(samplePath))
            {
                var (ok, frames) = WoWApi.TryLoadAddonDirectory(samplePath);
                Console.WriteLine($"Program: sample addon load success={ok}, frames={frames}");
            }
            else
            {
                Console.WriteLine($"Sample addon folder not found: {samplePath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WoW API initialization failed: {ex}");
        }

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
