using System;
using System.IO;

namespace FluxNew
{
    public static class WoWApi
    {
        // Install a minimal WoW-like API into the Lua state so addons can run.
        public static void LoadApi()
        {
            var lua = @"-- Flux minimal WoW API shim
Flux = Flux or {}
Flux._frames = Flux._frames or {}

local function join(...) local t={} for i=1,select('#',...) do t[i]=tostring(select(i,...)) end return table.concat(t,'\t') end
function print(...) io.write(join(...) .. '\n') end

function CreateFrame(ftype, name, parent)
  local frame = { _type=ftype, _name=name, _events = {}, scripts = {}, _points = {}, _children = {}, _shown = false }

  function frame:RegisterEvent(ev)
    self._events[ev]=true
    -- keep a weak-ish registry
    Flux._frames[#Flux._frames+1]=self
  end
  function frame:UnregisterEvent(ev) self._events[ev]=nil end
  function frame:SetScript(what, fn) self.scripts[what]=fn end
  function frame:Show() self._shown=true end
  function frame:Hide() self._shown=false end

  -- record size/position/scale operations so host can introspect
  function frame:SetPoint(point, relativeTo, relPoint, x, y)
    -- support optional args
    point = point or ''
    relativeTo = relativeTo or ''
    relPoint = relPoint or ''
    x = x or 0
    y = y or 0
    table.insert(self._points, { point=point, relativeTo = tostring(relativeTo), relPoint=relPoint, x = x, y = y })
  end
  function frame:SetSize(w,h)
    self.width = w
    self.height = h
  end
  function frame:SetWidth(w) self.width = w end
  function frame:SetHeight(h) self.height = h end
  function frame:SetScale(s) self.scale = s end

  -- parent/child relationship
  if parent ~= nil then
    table.insert(parent._children, frame)
    frame._parent = parent
  end

  return frame
end

function GetTime() return os.clock() end

function _DispatchEvent(ev, ...)
  for i,frame in ipairs(Flux._frames) do
    if frame._events and frame._events[ev] then
      local f = frame.scripts and frame.scripts['OnEvent']
      if f then pcall(f, frame, ev, ...) end
    end
  end
end
";

            KopiLuaRunner.TryRun(lua);
        }

        // Try to load an addon folder that contains a `main.lua` file.
        // Returns (success, framesSerialized) where framesSerialized is a ';' separated list of frame entries.
        public static (bool,string) TryLoadAddonDirectory(string addonPath)
        {
          try
          {
            var mainFile = Path.Combine(addonPath, "main.lua");
            if (!File.Exists(mainFile)) return (false, string.Empty);

            // Before running the addon, set up package.path so `require` can find
            // modules in the addon folder and the global `libs` directory.
            var addonDirForLua = addonPath.Replace('\\', '/');
            var libsRel = "./libs".Replace('\\', '/');
            var prelude =
              "package = package or {}\n" +
              "package.path = ('" + addonDirForLua + "/?.lua;" + addonDirForLua + "/?/init.lua;" + libsRel + "/?.lua;" + libsRel + "/?/init.lua;' .. (package.path or ''))\n" +
              "if type(package.cpath) ~= 'nil' then\n" +
              "  package.cpath = (\'" + libsRel + "/?.dll;" + libsRel + "/?.so;\' .. (package.cpath or ''))\n" +
              "end\n";

            var (pOk, pRes) = KopiLuaRunner.TryRun(prelude);
            if (!pOk)
            {
              Console.WriteLine($"Warning: failed to set package.path for addon '{Path.GetFileName(addonPath)}': {pRes}");
            }

            var code = File.ReadAllText(mainFile);
            var (ok, res) = KopiLuaRunner.TryRun(code);
            Console.WriteLine($"Loaded addon '{Path.GetFileName(addonPath)}': success={ok}, result={res}");

            // Development shortcut: if a static sample JSON exists (sample_frames.json),
            // use it and skip running the serializer. This lets UI work continue without
            // relying on runtime serializer execution.
            try
            {
              var samplePath = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "sample_frames.json");
              if (File.Exists(samplePath))
              {
                var sampleContents = File.ReadAllText(samplePath);
                if (!string.IsNullOrWhiteSpace(sampleContents))
                {
                  Console.WriteLine($"Using sample_frames.json for addon '{Path.GetFileName(addonPath)}' (len={sampleContents.Length})");
                  return (true, sampleContents);
                }
              }
            }
            catch (Exception ex)
            {
              Console.WriteLine($"Error reading sample_frames.json: {ex.Message}");
            }

            // Attempt to run an external Lua serializer shipped with the app (serialize_frames.lua)
            try
            {
                var serializerPath = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "serialize_frames.lua");
                if (File.Exists(serializerPath))
                {
                  // Execute the serializer file on the Lua VM and return its result.
                  // Use loadfile with long-bracket quoting for the path so backslashes are safe.
                  var luaCommand = $@"
        local f, err = loadfile([===[{serializerPath}]===])
        if not f then return tostring(err) end
        local ok, res = pcall(f)
        if not ok then error(res) end
        return res
        ";

                  // Prefer invoking the serializer file directly via KopiLua C-API when possible.
                    try
                    {
                        var (okFile, outFile) = KopiLuaRunner.TryRunFile(serializerPath);
                        Console.WriteLine($"Serializer raw (len={(outFile?.Length ?? 0)}): {(outFile != null && outFile.Length > 200 ? outFile.Substring(0,200) + "..." : outFile)}");
                        if (okFile && !string.IsNullOrWhiteSpace(outFile))
                        {
                            Console.WriteLine($"Addon '{Path.GetFileName(addonPath)}' frames (json): {outFile}");
                            return (true, outFile ?? string.Empty);
                        }
                        // Fall back to executing the wrapper via TryRun (which may print or return)
                        string? captured = null;
                        (bool qOk, string? qRes) = (false, null);
                        var originalOut = Console.Out;
                        var originalErr = Console.Error;
                        try
                        {
                            using (var sw = new System.IO.StringWriter())
                            {
                                Console.SetOut(sw);
                                Console.SetError(sw);
                                (qOk, qRes) = KopiLuaRunner.TryRun(luaCommand);
                                // flush and capture
                                Console.Out.Flush();
                                captured = sw.ToString();
                            }
                        }
                        finally
                        {
                            try { Console.SetOut(originalOut); } catch { }
                            try { Console.SetError(originalErr); } catch { }
                        }

                        // Prefer captured output if it looks like JSON (starts with '[' or '{')
                        var finalResult = qRes;
                        if (!string.IsNullOrWhiteSpace(captured))
                        {
                            var trimmed = captured.TrimStart();
                            if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
                            {
                                finalResult = captured;
                            }
                            else if (string.IsNullOrWhiteSpace(qRes))
                            {
                                finalResult = captured;
                            }
                        }

                        // Log raw result for diagnostics
                        Console.WriteLine($"Serializer raw (len={(finalResult?.Length ?? 0)}): {(finalResult != null && finalResult.Length > 200 ? finalResult.Substring(0,200) + "..." : finalResult)}");
                        if (qOk || !string.IsNullOrWhiteSpace(finalResult))
                        {
                            Console.WriteLine($"Addon '{Path.GetFileName(addonPath)}' frames (json): {finalResult}");
                            return (true, finalResult ?? string.Empty);
                        }
                        else
                        {
                          Console.WriteLine($"Serializer execution failed for addon '{Path.GetFileName(addonPath)}': {qRes}");
                          // Fallback: check for file written by serializer (debug_frames.json) in working directory
                          try
                          {
                            var dumpPath = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "debug_frames.json");
                            if (File.Exists(dumpPath))
                            {
                              var fileContents = File.ReadAllText(dumpPath);
                              if (!string.IsNullOrWhiteSpace(fileContents))
                              {
                                Console.WriteLine($"Found serializer file at {dumpPath} (len={fileContents.Length})");
                                return (true, fileContents);
                              }
                            }
                          }
                          catch (Exception ex)
                          {
                            Console.WriteLine($"Error reading debug_frames.json fallback: {ex.Message}");
                          }

                          return (false, string.Empty);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Error invoking serializer file via KopiLuaRunner.TryRunFile: " + ex.Message);
                        return (false, string.Empty);
                    }
                }
                else
                {
                    Console.WriteLine("serialize_frames.lua not found in output directory; no frame metadata returned.");
                    return (ok, string.Empty);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error running serializer: {ex}");
                return (false, string.Empty);
            }
          }
          catch (Exception ex)
          {
            Console.WriteLine($"Error loading addon: {ex}");
            return (false, string.Empty);
          }
        }
    }
}
