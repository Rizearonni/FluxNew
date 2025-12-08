-- serialize_frames.lua
-- Produces a JSON array describing Flux._frames for the host
local function q(s) return string.format("%q", tostring(s or "")) end
local out = {}
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
      local ar = q(tostring(p.relativeTo or ""))
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
    local parent = q((f.parent and f.parent._name) or '')
    entry = '{"name":'..name..',"type":'..ftype..',"text":'..text..',"parent":'..parent..'}'
  else
    entry = '{"name":'..name..',"type":'..ftype..',"shown":'..shown..',"x":'..x..',"y":'..y..',"w":'..w..',"h":'..h..',"scale":'..scale..',"anchors":'..anchorStr..',"children":'..childStr..'}'
  end
  table.insert(out, entry)
end
local result = '['..table.concat(out,',')..']'
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
