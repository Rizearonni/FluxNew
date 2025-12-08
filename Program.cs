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
        // If launched with --load-addon <path> then run the loader headless
        if (args != null && args.Length >= 2 && string.Equals(args[0], "--load-addon", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var addonPath = args[1];
                Console.WriteLine($"Loading addon from: {addonPath}");
                var (ok, frames) = WoWApi.TryLoadAddonDirectory(addonPath);
                Console.WriteLine($"TryLoadAddonDirectory: success={ok}, framesLen={(frames?.Length ?? 0)}");
                if (!string.IsNullOrEmpty(frames)) Console.WriteLine(frames);
                // If this looks like Attune, try to force-open its UI (call Attune_Frame)
                try
                {
                    if (addonPath != null && addonPath.IndexOf("Attune", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flux_invoke_attune_" + Guid.NewGuid().ToString("N") + ".lua");
                            try { System.IO.File.WriteAllText(tmp, "if Attune_Frame then Attune_Frame() end"); var (okI, outI) = KopiLuaRunner.TryRunFile(tmp); } catch { } finally { try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { } }
                        }
                        catch { }
                        // attempt to run the serializer directly again to capture frames after UI open
                        var serializerPath = System.IO.Path.Combine(AppContext.BaseDirectory ?? System.Environment.CurrentDirectory, "serialize_frames.lua");
                        try
                        {
                            if (System.IO.File.Exists(serializerPath))
                            {
                                var (okS, outS) = KopiLuaRunner.TryRunFile(serializerPath);
                                Console.WriteLine($"Serializer raw (len={(outS?.Length ?? 0)}): {(outS != null && outS.Length>200? outS.Substring(0,200) + "...": outS)}");
                                if (!string.IsNullOrWhiteSpace(outS)) Console.WriteLine(outS);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading addon: {ex}");
            }
            return;
        }

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
