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
        
        // Generate placeholder textures if they don't exist
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var checkPath = Path.Combine(baseDir, "Interface", "Buttons", "UI-CheckBox-Up.tga");
            if (!File.Exists(checkPath))
            {
                Console.WriteLine("Generating placeholder textures...");
                TestTextureGenerator.GeneratePlaceholderTextures(baseDir);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to generate placeholder textures: {ex.Message}");
        }
        
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
                            try {
                                var script = "-- Aggressive Attune fixer: expose attunelocal_tree, synthesize fallback, and update TreeGroup widgets\n" +
                                             "local function try_extract_upvalue(fn, name)\n" +
                                             "  if not (fn and debug and debug.getupvalue) then return nil end\n" +
                                             "  for i=1,200 do local n,v = debug.getupvalue(fn, i); if not n then break end; if n == name then return v end end\n" +
                                             "  return nil\n" +
                                             "end\n" +
                                             "-- try to extract attunelocal_tree from Attune_LoadTree or any nearby function\n" +
                                             "if Attune_LoadTree then\n" +
                                             "  local up = try_extract_upvalue(Attune_LoadTree, 'attunelocal_tree')\n" +
                                             "  if up then _G.attunelocal_tree = up end\n" +
                                             "  -- call it to ensure it populates/updates internal state\n" +
                                             "  pcall(function() Attune_LoadTree() end)\n" +
                                             "  -- re-check after call\n" +
                                             "  if not _G.attunelocal_tree then local up2 = try_extract_upvalue(Attune_LoadTree, 'attunelocal_tree'); if up2 then _G.attunelocal_tree = up2 end end\n" +
                                             "end\n" +
                                             "-- debug outputs\n" +
                                             "if Attune_Data then print('FLUX-DBG: Attune_Data.attunes=' .. tostring(Attune_Data and #Attune_Data.attunes or 'nil')) end\n" +
                                             "if Attune_DB then print('FLUX-DBG: Attune_DB present') end\n" +
                                             "-- If we still don't have an attunelocal_tree, attempt to synthesize one from Attune_Data.attunes\n" +
                                             "local function synth_from_data()\n" +
                                             "  if not (Attune_Data and Attune_Data.attunes and type(Attune_Data.attunes) == 'table') then return nil end\n" +
                                             "  local synth = {}\n" +
                                             "  local expacMap = {}\n" +
                                             "  for i,a in pairs(Attune_Data.attunes) do\n" +
                                             "    local exp = tostring(a.EXPAC or 'Unknown')\n" +
                                             "    local grp = tostring(a.GROUP or 'Default')\n" +
                                             "    expacMap[exp] = expacMap[exp] or {}\n" +
                                             "    local gmap = expacMap[exp]\n" +
                                             "    gmap[grp] = gmap[grp] or { value = grp, text = grp, children = {} }\n" +
                                             "    local text = a.NAME or tostring(a.ID)\n" +
                                             "    table.insert(gmap[grp].children, { value = a.ID, text = text })\n" +
                                             "  end\n" +
                                             "  for exp, groups in pairs(expacMap) do\n" +
                                             "    local expNode = { value = exp, text = exp, children = {} }\n" +
                                             "    for _, g in pairs(groups) do table.insert(expNode.children, g) end\n" +
                                             "    table.insert(synth, expNode)\n" +
                                             "  end\n" +
                                             "  return synth\n" +
                                             "end\n" +
                                             "if not _G.attunelocal_tree or #(_G.attunelocal_tree or {}) == 0 then\n" +
                                             "  local s = synth_from_data()\n" +
                                             "  if s and #s > 0 then\n" +
                                             "    print('FLUX-DBG: synthesized tree count=' .. tostring(#s))\n" +
                                             "    _G.attunelocal_tree = s\n" +
                                             "  else\n" +
                                             "    print('FLUX-DBG: no synth tree available')\n" +
                                             "  end\n" +
                                             "end\n" +
                                             "-- Propagate to any existing AceGUI TreeGroup widgets: set their tree if empty\n" +
                                             "pcall(function()\n" +
                                             "  local gui = (LibStub and LibStub._libs and LibStub._libs['AceGUI-3.0']) or nil\n" +
                                             "  if gui and gui._widgets then\n" +
                                             "    for _,w in ipairs(gui._widgets) do\n" +
                                             "      if w and w._kind == 'TreeGroup' then\n" +
                                             "        pcall(function() if not w.tree or #w.tree == 0 then w:SetTree(_G.attunelocal_tree) end end)\n" +
                                             "      end\n" +
                                             "    end\n" +
                                             "  end\n" +
                                             "end)\n" +
                                             "print('FLUX-DBG: after Attune fixer attunelocal_tree=' .. tostring(#(_G.attunelocal_tree or {})))\n" +
                                             "if Attune_Frame then pcall(Attune_Frame) end";
                                System.IO.File.WriteAllText(tmp, script);
                                var (okI, outI) = KopiLuaRunner.TryRunFile(tmp);
                            } catch { } finally { try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { } }
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

        // If launched with --test-blp <path> then attempt to decode the file via TextureCache
        if (args != null && args.Length >= 2 && string.Equals(args[0], "--test-blp", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var testPath = args[1];
                Console.WriteLine($"Testing texture decode for: {testPath}");
                var td = FluxNew.TextureCache.LoadTexture(testPath);
                if (td == null)
                {
                    Console.WriteLine("TextureCache: returned null (failed to load or unsupported)");
                }
                else
                {
                    Console.WriteLine($"TextureCache: success -> {td.Width}x{td.Height}, bytes={td.Rgba?.Length}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error running --test-blp: {ex.Message}");
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
