-- Register post-load fixers for specific addons. These are executed
-- after the addon receives ADDON_LOADED; fixers receive the addon name.
if not Flux or not Flux.RegisterPostLoadFixer then return end

-- Attune fixer: expose attunelocal_tree (if hidden), call Attune_LoadTree,
-- synthesize fallback from Attune_Data.attunes, and apply to AceGUI TreeGroup widgets.
Flux.RegisterPostLoadFixer('Attune', function(addonName)
  if tostring(addonName or '') ~= 'Attune' then return end
  local function try_extract_upvalue(fn, name)
    if not (fn and debug and debug.getupvalue) then return nil end
    for i=1,200 do local n,v = debug.getupvalue(fn, i); if not n then break end; if n == name then return v end end
    return nil
  end

  if Attune_LoadTree then
    local up = try_extract_upvalue(Attune_LoadTree, 'attunelocal_tree')
    if up then _G.attunelocal_tree = up end
    pcall(function() Attune_LoadTree() end)
    if not _G.attunelocal_tree then local up2 = try_extract_upvalue(Attune_LoadTree, 'attunelocal_tree'); if up2 then _G.attunelocal_tree = up2 end end
  end

  if Attune_Data then pcall(function() print('FLUX-DBG: Attune_Data.attunes=' .. tostring(Attune_Data and #Attune_Data.attunes or 'nil')) end) end
  if Attune_DB then pcall(function() print('FLUX-DBG: Attune_DB present') end) end

  local function synth_from_data()
    if not (Attune_Data and Attune_Data.attunes and type(Attune_Data.attunes) == 'table') then return nil end
    local synth = {}
    local expacMap = {}
    for i,a in pairs(Attune_Data.attunes) do
      local exp = tostring(a.EXPAC or 'Unknown')
      local grp = tostring(a.GROUP or 'Default')
      expacMap[exp] = expacMap[exp] or {}
      local gmap = expacMap[exp]
      gmap[grp] = gmap[grp] or { value = grp, text = grp, children = {} }
      local text = a.NAME or tostring(a.ID)
      table.insert(gmap[grp].children, { value = a.ID, text = text })
    end
    for exp, groups in pairs(expacMap) do
      local expNode = { value = exp, text = exp, children = {} }
      for _, g in pairs(groups) do table.insert(expNode.children, g) end
      table.insert(synth, expNode)
    end
    return synth
  end

  if not _G.attunelocal_tree or #(_G.attunelocal_tree or {}) == 0 then
    local s = synth_from_data()
    if s and #s > 0 then
      pcall(function() print('FLUX-DBG: synthesized tree count=' .. tostring(#s)) end)
      _G.attunelocal_tree = s
    else
      pcall(function() print('FLUX-DBG: no synth tree available') end)
    end
  end

  pcall(function()
    local gui = (LibStub and LibStub._libs and LibStub._libs['AceGUI-3.0']) or nil
    if gui and gui._widgets then
      for _,w in ipairs(gui._widgets) do
        if w and w._kind == 'TreeGroup' then
          pcall(function() if not w.tree or #w.tree == 0 then w:SetTree(_G.attunelocal_tree) end end)
        end
      end
    end
  end)

  pcall(function() print('FLUX-DBG: after Attune fixer attunelocal_tree=' .. tostring(#(_G.attunelocal_tree or {}))) end)
  pcall(function() if Attune_Frame then Attune_Frame() end end)
end)
