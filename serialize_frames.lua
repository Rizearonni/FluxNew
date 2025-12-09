-- serialize_frames.lua
-- Produces a JSON array describing Flux._frames for the host
local function q(s) return string.format("%q", tostring(s or "")) end
local out = {}
local __anon_counter = 0
local function ensure_name(tbl)
  if type(tbl) ~= "table" then return tostring(tbl or "") end
  if tbl._name and tbl._name ~= "" then return tbl._name end
  -- try to find the table in Flux._frames
  for _,ff in ipairs(Flux._frames) do
    if ff == tbl then
      if not ff._name or ff._name == "" then
        __anon_counter = __anon_counter + 1
        ff._name = "__anon_" .. tostring(__anon_counter)
      end
      return ff._name
    end
  end
  -- not found in Flux._frames: assign a synthetic name and emit a minimal placeholder frame
  __anon_counter = __anon_counter + 1
  local nm = "__anon_" .. tostring(__anon_counter)
  tbl._name = nm
  -- add a minimal placeholder so hosts can resolve anchors to this name
  table.insert(out, '{"name":'..q(nm)..',"type":"Frame","shown":false,"x":0,"y":0,"w":0,"h":0,"scale":1,"nodePath":'..q("")..',"anchors":[],"children":[]}')
  return nm
end
for i,f in ipairs(Flux._frames) do
  local name = q(f._name or "")
  local ftype = q(f._type or "")
  local shown = (f._shown and "true" or "false")
  local x = tostring(f.x or f.left or 0)
  local y = tostring(f.y or f.top or 0)
  local w = tostring(f.width or f.w or 0)
  local h = tostring(f.height or f.h or 0)
  local scale = tostring(f.scale or 1)
  local anchors = {}
  if f._points then
    for _,p in ipairs(f._points) do
      local ap = q(p.point or "")
      -- Extract frame name from relativeTo (it's a frame table)
      local relFrame = p.relativeTo
      local ar = ""
      if type(relFrame) == "table" then
        ar = q(ensure_name(relFrame))
      elseif type(relFrame) == "string" then
        local s = relFrame
        local matched = false
        if s:match("^table:%s*0x") then
          -- Prefer the runtime-provided mapping (set by the shim): Flux._tostring_map[tostring(frame)] = name
          local mapped = nil
          if Flux and Flux._tostring_map then mapped = Flux._tostring_map[s] end
          if mapped then
            ar = q(mapped)
            matched = true
          else
            -- Fallback: try scanning Flux._frames for a table whose tostring() matches
            for _,ff in ipairs(Flux._frames) do
              if tostring(ff) == s then
                ar = q(ensure_name(ff))
                matched = true
                break
              end
            end
            if not matched then
              -- Synthesize a stable name based on the hex addr when possible
              local hex = s:match("^table:%s*(0x[%x]+)")
              local synthName
              if hex then
                synthName = "__synth_" .. hex
              else
                __anon_counter = (__anon_counter or 0) + 1
                synthName = "__anon_" .. __anon_counter
              end
              -- Record mapping so future passes can resolve quickly
              Flux._tostring_map = Flux._tostring_map or {}
              Flux._tostring_map[s] = synthName
              ar = q(synthName)
              -- Emit a minimal placeholder frame entry once
              _G.__synth_emitted = _G.__synth_emitted or {}
              if not _G.__synth_emitted[synthName] then
                _G.__synth_emitted[synthName] = true
                table.insert(out, '{"name":'..q(synthName)..',"type":"Frame","shown":false,"x":0,"y":0,"w":0,"h":0,"scale":1,"nodePath":'..q('')..',"anchors":[],"children":[]}')
              end
              -- Log unresolved token for visibility
              pcall(function() print("FLUX-DBG: Serializer unresolved tostring="..s.." synthesized="..synthName) end)
            end
          end
        else
          ar = q(s)
        end
      else
        ar = q(tostring(relFrame or ""))
      end
      local arp = q(p.relPoint or "")
      local ax = tostring(p.x or 0)
      local ay = tostring(p.y or 0)
      table.insert(anchors, '{"point":'..ap..',"relativeTo":'..ar..',"relPoint":'..arp..',"x":'..ax..',"y":'..ay..'}')
    end
  end
  local anchorStr = '['..table.concat(anchors,',')..']'
  local children = {}
  if f._children then
    for _,c in ipairs(f._children) do
      table.insert(children, q(c._name or ""))
    end
  end
  local childStr = '['..table.concat(children,',')..']'
  local entry = nil
  if (f._type or '') == 'FontString' then
    local text = q(f.text or f[1] or '')
    local parent = f.parent and f.parent._name or ''
    local parentNodePath = ''
    if f.parent and f.parent._nodePath then parentNodePath = tostring(f.parent._nodePath) end
    entry = '{"name":'..name..',"type":'..ftype..',"text":'..text..',"parent":'..q(parent)..',"parentNodePath":'..q(parentNodePath)..'}'
  elseif (f._type or '') == 'Texture' then
    -- Serialize texture metadata
    local texPath = q(f.texturePath or '')
    local layer = q(f.layer or 'ARTWORK')
    local parent = f.parent and f.parent._name or ''
    local coords = f.texcoords or {0,1,0,1}
    local coordStr = '['..table.concat(coords,',')..']'
    entry = '{"name":'..name..',"type":'..ftype..',"texturePath":'..texPath..',"layer":'..layer..',"parent":'..q(parent)..',"x":'..x..',"y":'..y..',"w":'..w..',"h":'..h..',"texcoords":'..coordStr..',"anchors":'..anchorStr..'}'
  else
    -- include nodePath when available (populated by SetTree materialization)
    local nodePath = f._nodePath or ''
    entry = '{"name":'..name..',"type":'..ftype..',"shown":'..shown..',"x":'..x..',"y":'..y..',"w":'..w..',"h":'..h..',"scale":'..scale..',"nodePath":'..q(nodePath)..',"anchors":'..anchorStr..',"children":'..childStr..'}'
  end
  table.insert(out, entry)
end
local result = '['..table.concat(out,',')..']'

-- Post-process: convert any remaining stringified table anchors like
-- "relativeTo":"table: 0xabcdef" into stable synthetic names
-- and append minimal placeholder entries so hosts can resolve them.
local replacements = 0
result, replacements = result:gsub('("relativeTo":")table:%s*(0x[%x]+)(")', '%1__synth_%2%3')
if replacements > 0 then
  local seen = {}
  local placeholders = {}
  for hex in result:gmatch('__synth_(0x[%x]+)') do
    if not seen[hex] then
      seen[hex] = true
      table.insert(placeholders, string.format('{"name":"__synth_%s","type":"Frame","shown":false,"x":0,"y":0,"w":0,"h":0,"scale":1,"nodePath":"","anchors":[],"children":[]}', hex))
    end
  end
  if #placeholders > 0 then
    -- insert placeholders before final closing bracket
    local insertStr = ',' .. table.concat(placeholders, ',') .. ']'
    result = result:gsub('%]$', insertStr)
  end
end
-- Also print the JSON so runtimes that capture stdout receive it reliably
-- Also write the JSON to a file so hosts can read it if stdout/return isn't available
local ok, err = pcall(function()
  local f = io.open("debug_frames.json", "w+")
  if f then
    f:write(result)
    f:close()
  end
end)
if not ok then
  -- ignore file write errors
end

print(result)
return result
