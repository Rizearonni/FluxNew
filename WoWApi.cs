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

function CreateFrame(ftype, name)
  local frame = { _type=ftype, _name=name, _events = {}, scripts = {} }
  function frame:RegisterEvent(ev)
    self._events[ev]=true
    -- keep a weak-ish registry
    Flux._frames[#Flux._frames+1]=self
  end
  function frame:UnregisterEvent(ev) self._events[ev]=nil end
  function frame:SetScript(what, fn) self.scripts[what]=fn end
  function frame:Show() self._shown=true end
  function frame:Hide() self._shown=false end
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
        public static bool TryLoadAddonDirectory(string addonPath)
        {
            try
            {
                var mainFile = Path.Combine(addonPath, "main.lua");
                if (!File.Exists(mainFile)) return false;
                var code = File.ReadAllText(mainFile);
                var (ok, res) = KopiLuaRunner.TryRun(code);
                Console.WriteLine($"Loaded addon '{Path.GetFileName(addonPath)}': success={ok}, result={res}");
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading addon: {ex}");
                return false;
            }
        }
    }
}
