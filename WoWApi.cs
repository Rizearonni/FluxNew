using System;
using System.IO;

namespace FluxNew
{
    public static class WoWApi
    {
      // Simple expansion preset structure used by the UI to configure project/build values
      public class ExpansionPreset
      {
        public int WOW_PROJECT_ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Build { get; set; } = string.Empty;
        public string TOC { get; set; } = string.Empty;
      }

      // In-memory presets (editable from the PresetEditorWindow)
      private static readonly System.Collections.Generic.Dictionary<string, ExpansionPreset> _expansionPresets =
        new System.Collections.Generic.Dictionary<string, ExpansionPreset>(System.StringComparer.OrdinalIgnoreCase)
        {
          { "vanilla", new ExpansionPreset { WOW_PROJECT_ID = 2, Name = "Vanilla", Version = "1.12.1", Build = "3355", TOC = "11200" } },
          { "mists", new ExpansionPreset { WOW_PROJECT_ID = 4, Name = "Mists", Version = "5.4.8", Build = "18414", TOC = "50400" } },
          { "mainline", new ExpansionPreset { WOW_PROJECT_ID = 1, Name = "Mainline", Version = "10.0.0", Build = "100000", TOC = "100000" } }
        };

        // Install a minimal WoW-like API into the Lua state so addons can run.
        public static void LoadApi()
        {
            var lua = @"-- Flux minimal WoW API shim
Flux = Flux or {}
Flux._frames = Flux._frames or {}
Flux._host_calls = Flux._host_calls or {}
-- Post-load fixer registry: allow external scripts to register functions
-- that will run after an addon is loaded so they can patch or synthesize
-- data (useful for addons that keep data in local upvalues or populate
-- their trees lazily). Fixers receive the addon name as a parameter.
Flux._post_load_fixers = Flux._post_load_fixers or {}
-- Register a post-load fixer. Optional `opts` table may include:
--  * enabled (boolean, default true)
--  * order (number, default 1000) - lower numbers run earlier
function Flux.RegisterPostLoadFixer(name, fn, opts)
  if type(fn) ~= 'function' then return false end
  opts = opts or {}
  local rec = { name = tostring(name or ''), fn = fn, opts = { enabled = (opts.enabled ~= false), order = tonumber(opts.order) or 1000 } }
  table.insert(Flux._post_load_fixers, rec)
  return true
end

-- Apply registered fixers for the given addon name. Returns how many ran.
function Flux.ApplyPostLoadFixers(addonName)
  local list = {}
  for i,rec in ipairs(Flux._post_load_fixers or {}) do
    table.insert(list, rec)
  end
  -- sort by order ascending
  table.sort(list, function(a,b) return (a.opts and a.opts.order or 1000) < (b.opts and b.opts.order or 1000) end)
  local ran = 0
  for _,rec in ipairs(list) do
    if not rec.opts or rec.opts.enabled then
      pcall(function()
        rec.fn(addonName)
        ran = ran + 1
      end)
    end
  end
  return ran
end

-- convenience aliases used by some addons
if not tinsert then tinsert = table.insert end
if not tremove then tremove = table.remove end
if not unpack then unpack = table.unpack or unpack end
-- common WoW globals
UISpecialFrames = UISpecialFrames or {}
TreeExpandStatus = TreeExpandStatus or {}

-- Minimal LibStub + AceLocale stubs to satisfy common WoW addon dependencies
LibStub = LibStub or {}
LibStub._libs = LibStub._libs or {}
setmetatable(LibStub, { __call = function(self, name)
  if not self._libs[name] then
    local lib = {}
    function lib:NewLibrary(ver)
      local t = {}
      self._libs[name] = t
      return t
    end
    function lib:GetLibrary()
      return self._libs[name]
    end
    self._libs[name] = lib
  end
  return self._libs[name]
end })

-- Provide a simple AceLocale-3.0 implementation via LibStub
do
  local ace = LibStub('AceLocale-3.0') or {}
  function ace:NewLocale(addon, locale, isDefault)
    local t = {}
    -- ensure default locale tables return keys (or empty strings) instead of nil
    local mt = { __index = function(_,k) return tostring(k) end }
    if isDefault then
      setmetatable(t, mt)
      _G[addon .. 'Lang'] = t
      return t
    end
    setmetatable(t, mt)
    return t
  end
  LibStub._libs['AceLocale-3.0'] = ace
end

  -- Provide a global GetLocale() similar to WoW API and AceLocale:GetLocale
  do
    function GetLocale()
      return 'enUS'
    end
    local ace = LibStub('AceLocale-3.0') or {}
    function ace:GetLocale(addon, silent)
      local key = addon .. 'Lang'
      if _G[key] then return _G[key] end
      local t = {}
      setmetatable(t, { __index = function(_,k) return tostring(k) end })
      _G[key] = t
      return _G[key]
    end
    LibStub._libs['AceLocale-3.0'] = ace
  end

  -- Ensure any existing <Addon>Lang tables have a safe metatable
  do
    for k,v in pairs(_G) do
      if type(k) == 'string' and string.sub(k, -4) == 'Lang' and type(v) == 'table' then
        if not getmetatable(v) then
          setmetatable(v, { __index = function(_,kk) return tostring(kk) end })
        end
      end
    end
  end

  -- Some addons expect specific globals; provide safe defaults to avoid nil concatenation
  attunelocal_game_version = attunelocal_game_version or ''
  attunelocal_game_patch = attunelocal_game_patch or ''

  -- Minimal AceAddon-3.0 stub: provide NewAddon and common lifecycle methods
  do
    local addonLib = LibStub('AceAddon-3.0') or {}
    function addonLib:NewAddon(name, ...)
      local obj = { _name = name }
      function obj:GetName() return obj._name end
      function obj:RegisterEvent(ev, handler)
        -- No-op: host's CreateFrame instrumentation handles events if needed
      end
      function obj:RegisterMessage(msg, handler) end
      function obj:RegisterComm(prefix, handler) end
      function obj:SendCommMessage(prefix, msg, channel, target) end
      function obj:UnregisterEvent(ev) end
      function obj:Print(...) io.write(table.concat({...}, '\t') .. '\n') end
      -- Provide placeholders for addon lifecycle
      obj.OnInitialize = obj.OnInitialize or nil
      obj.OnEnable = obj.OnEnable or nil
      -- register addon so the shim can trigger lifecycle methods on ADDON_LOADED
      addonLib._addons = addonLib._addons or {}
      addonLib._addons[name] = obj
      return obj
    end
    LibStub._libs['AceAddon-3.0'] = addonLib
  end

  -- Minimal AceGUI stub: permissive factory that returns proxy widgets with no-op methods
  do
      local acegui = {}
      local function makeWidget(kind, fname)
        local widget = {}
        widget.type = kind
        widget.children = {}
        -- create an underlying frame so serializer sees a CreateFrame call
        local cname = tostring(fname or (kind .. '_' .. tostring(math.random(100000,999999))))
        widget.frame = CreateFrame('Frame', cname, nil)
        -- Special-case Label widgets: create a FontString immediately so callers
        -- that expect a Label to expose text get a concrete FontString object.
        if kind == 'Label' then
          widget._isLabel = true
          widget.frame._fontstrings = widget.frame._fontstrings or {}
          local fs = widget.frame:CreateFontString('LabelText')
          fs.text = ''
          table.insert(widget.frame._fontstrings, fs)
          -- convenience SetText that updates the attached FontString
          function widget:SetText(t)
            local s = tostring(t or '')
            fs.text = s
            self.text = s
          end
          function widget:GetText()
            return fs.text
          end
          function widget:Show() if widget.frame and widget.frame.Show then widget.frame:Show() end end
          function widget:Hide() if widget.frame and widget.frame.Hide then widget.frame:Hide() end end
          function widget:SetPoint(...) if widget.frame and widget.frame.SetPoint then widget.frame:SetPoint(...) end end
          function widget:SetSize(w,h) if widget.frame and widget.frame.SetSize then widget.frame:SetSize(w,h) end end
        end
        -- expose a content object similar to AceGUI containers so addons can index .content.obj.frame
        widget.content = widget.content or { obj = { frame = widget.frame } }

        -- For Frame containers create common internal child frames (close button, status background)
        if kind == 'Frame' then
          local closebtn = CreateFrame('Frame', cname .. '_closebutton', widget.frame)
          local statusbg = CreateFrame('Frame', cname .. '_statusbg', widget.frame)
          -- record as children so GetChildren returns them in order
          widget.frame._children = widget.frame._children or {}
          table.insert(widget.frame._children, closebtn)
          table.insert(widget.frame._children, statusbg)
        end
        pcall(function() print('FLUX-DBG: AceGUI Create ' .. tostring(kind) .. ' name=' .. tostring(cname)) end)

        function widget:AddChild(child)
          if type(child) == 'table' and child.frame then
            table.insert(widget.children, child)
            table.insert(widget.content, child)
            widget.frame._children = widget.frame._children or {}
            table.insert(widget.frame._children, child.frame)
            child.frame._parent = widget.frame
          end
        end

        function widget:SetTitle(t) self.title = t end
        function widget:SetStatusText(t) self.statusText = t end
        function widget:SetResizeBounds(minw, minh, maxw, maxh) self.resizeBounds = {minw=minw,minh=minh,maxw=maxw,maxh=maxh} end
        function widget:SetLayout(...) self.layout = {...} end
        function widget:SetCallback(k, fn) self._callbacks = self._callbacks or {} self._callbacks[k] = fn end
        function widget:SetList(list) self.list = list end
        function widget:SetValue(v) self.value = v end
        function widget:SetText(t)
          self.text = t
          if self.frame then
            self.frame._fontstrings = self.frame._fontstrings or {}
            table.insert(self.frame._fontstrings, { text = tostring(t) })
          end
        end

        -- common layout helpers used by TreeGroup/containers
        function widget:SetFullWidth(v) self.fullWidth = v end
        function widget:SetFullHeight(v) self.fullHeight = v end
        function widget:SetAutoAdjustHeight(v) self.autoAdjustHeight = v end
        function widget:SetAutoAdjustWidth(v) self.autoAdjustWidth = v end
        function widget:SelectByPath(path)
          -- Normalize path to array of parts
          local parts = {}
          if type(path) == 'string' then
            for p in string.gmatch(path, '[^/|]+') do table.insert(parts, p) end
          elseif type(path) == 'table' then
            for i=1,#path do table.insert(parts, path[i]) end
          else
            table.insert(parts, tostring(path))
          end
          self.selectedPath = parts
          -- Try to resolve into a node stored on the tree (we attach _frame on SetTree)
          local function find_node(nodes, idx)
            if not nodes or #nodes == 0 then return nil end
            local key = parts[idx]
            for i,n in ipairs(nodes) do
              if tostring(n.text or n.name or i) == tostring(key) or tostring(i) == tostring(key) then
                if idx == #parts then return n end
                return find_node(n.children or {}, idx + 1)
              end
            end
            return nil
          end
          if self.tree then
            local node = find_node(self.tree, 1)
            self.selectedNode = node
            -- record selection in localstatus.groups for host inspection
            self.localstatus = self.localstatus or { groups = {} }
            self.localstatus.selected = (node ~= nil)
            if node and node._frame then
              node._frame._selected = true
            end
            -- set expand status for the path
            if #parts > 0 then TreeExpandStatus = TreeExpandStatus or {}; TreeExpandStatus[table.concat(parts, '/')] = true end
          end
        end

        function widget:ReleaseChildren()
          -- hide and detach child frames, clear child lists
          if self.children then
            for _, c in ipairs(self.children) do
              if type(c) == 'table' and c.frame and c.frame._parent then
                c.frame._parent = nil
              end
            end
          end
          self.children = {}
          self.content = { obj = { frame = self.frame } }
          self.frame._children = {}
        end

        function widget:SetPoint(...) if widget.frame and widget.frame.SetPoint then widget.frame:SetPoint(...) end end
        function widget:SetSize(w,h) if widget.frame and widget.frame.SetSize then widget.frame:SetSize(w,h) end end
        function widget:SetWidth(w) if widget.frame and widget.frame.SetWidth then widget.frame:SetWidth(w) end end
        function widget:SetHeight(h) if widget.frame and widget.frame.SetHeight then widget.frame:SetHeight(h) end end
        function widget:SetScale(s) if widget.frame and widget.frame.SetScale then widget.frame:SetScale(s) end end
        function widget:Show() if widget.frame and widget.frame.Show then widget.frame:Show() end end
        function widget:Hide() if widget.frame and widget.frame.Hide then widget.frame:Hide() end end

        function widget:CreateFontString(name)
          local fs = { _type = 'FontString', _name = tostring(name or 'fs_' .. tostring(math.random(1000,9999))), text = '', parent = widget.frame }
          -- attach to the widget's frame so serializer can find it
          widget.frame._fontstrings = widget.frame._fontstrings or {}
          table.insert(widget.frame._fontstrings, fs)
          table.insert(Flux._frames, fs)
          return fs
        end
        function widget:CreateLine(name)
          local ln = { _type = 'Line', _name = tostring(name or 'line_' .. tostring(math.random(1000,9999))) }
          table.insert(Flux._frames, ln)
          return ln
        end

        -- expose an `obj` reference (many addons expect an `obj` field)
        widget.obj = widget.frame
        widget._proxy = widget
        widget._kind = kind
        acegui._widgets = acegui._widgets or {}
        table.insert(acegui._widgets, widget)

        -- TreeGroup specific helper
        function widget:SetTree(tree)
          self.tree = tree or {}
          self.localstatus = self.localstatus or { groups = {} }
          pcall(function() print('FLUX-DBG: SetTree count=' .. tostring(#self.tree or 0)) end)
          pcall(function() if debug and debug.traceback then print('FLUX-DBG: SetTree caller trace:\n' .. debug.traceback()) end end)

          -- Create node frames for existing nodes
          local function walk(nodes, parentFrame, prefix)
            for i,node in ipairs(nodes) do
              local nid = tostring(i)
              local nname = tostring(prefix or '') .. (node.text or ('node'..nid))
              local nodeFrame = CreateFrame('Frame', (widget.frame._name or '') .. '_node_' .. tostring(math.random(100000,999999)), parentFrame or widget.frame)
              pcall(function() print('FLUX-DBG: SetTree created node frame for ' .. tostring(node.text or nname)) end)
              node._frame = nodeFrame
              node._nodePath = (prefix or '') .. tostring(i)
              -- Mirror the node path onto the actual frame so the serializer can pick it up
              nodeFrame._nodePath = node._nodePath
              
              -- Create icon texture for the node (folder icon or expand/collapse indicator)
              local iconTex = nodeFrame:CreateTexture('node_icon', 'ARTWORK')
              if node.icon then
                -- Use node-specific icon if provided
                iconTex:SetTexture(node.icon)
              elseif node.children and #node.children > 0 then
                -- Parent node: use folder icon
                iconTex:SetTexture('Interface\\Buttons\\UI-PlusButton-Up')
              else
                -- Leaf node: use document icon  
                iconTex:SetTexture('Interface\\Icons\\INV_Misc_Note_01')
              end
              iconTex:SetSize(16, 16)
              iconTex:SetPoint('LEFT', nodeFrame, 'LEFT', 2, 0)
              
              -- attach a FontString with the node text
              local fs = nodeFrame:CreateFontString('node_label')
              fs.text = tostring(node.text or '')
              pcall(function() print('FLUX-DBG: Node label text=' .. tostring(fs.text)) end)
              -- ensure parent frame knows about this child
              nodeFrame._parent = parentFrame or widget.frame
              parentFrame = parentFrame or widget.frame
              parentFrame._children = parentFrame._children or {}
              table.insert(parentFrame._children, nodeFrame)
              -- record group placeholders for localstatus
              table.insert(self.localstatus.groups, { text = node.text or '', children = node.children and #node.children or 0 })
              if node.children then
                walk(node.children, nodeFrame, nname .. '/')
              end
            end
          end

          -- Watcher: intercept future table insertions so addons that populate the tree later are captured
          local function watch(tbl, parentFrame, prefix)
            if type(tbl) ~= 'table' then return end
            local mt = getmetatable(tbl) or {}
            if mt.__flux_watch then return end
            local old_newindex = mt.__newindex
            mt.__flux_watch = true
            mt.__newindex = function(t,k,v)
              if old_newindex then
                old_newindex(t,k,v)
              else
                rawset(t,k,v)
              end
              -- if a table assigned, recursively watch it
              if type(v) == 'table' then watch(v, parentFrame, (prefix or '') .. tostring(k) .. '/') end
              -- if numeric index added, materialize a node frame for it
              if type(k) == 'number' then
                local node = v
                local nodeFrame = CreateFrame('Frame', (widget.frame._name or '') .. '_node_' .. tostring(math.random(100000,999999)), parentFrame or widget.frame)
                node._frame = nodeFrame
                node._nodePath = (prefix or '') .. tostring(k)
                nodeFrame._nodePath = node._nodePath
                local fs = nodeFrame:CreateFontString('node_label')
                fs.text = tostring(node.text or '')
                nodeFrame._parent = parentFrame or widget.frame
                parentFrame = parentFrame or widget.frame
                parentFrame._children = parentFrame._children or {}
                table.insert(parentFrame._children, nodeFrame)
                table.insert(widget.localstatus.groups, { text = node.text or '', children = node.children and #node.children or 0 })
                if node.children then watch(node.children, nodeFrame, (prefix or '') .. tostring(k) .. '/') end
                pcall(function() print('FLUX-DBG: watch created node for key='..tostring(k)..' text='..tostring(node.text or '')) end)
              end
            end
            setmetatable(tbl, mt)
            -- recursively watch existing numeric elements
            for i,v in ipairs(tbl) do
              if type(v) == 'table' then watch(v, parentFrame, (prefix or '') .. tostring(i) .. '/') end
            end
          end

          -- If there are existing nodes, materialize them now; always set a watcher to catch future mutations
          pcall(function() if #self.tree > 0 then walk(self.tree, widget.frame, '') else pcall(function() print('FLUX-DBG: SetTree initial tree empty; attaching watcher') end) end end)
          pcall(function() watch(self.tree, widget.frame, '') end)
        end

          -- For TreeGroup widgets: attempt to invoke Attune_LoadTree (if present) so Attune's local tree is populated,
          -- then apply any populated `attunelocal_tree` to the widget. This ensures the AddOn's tree data
          -- is available when the UI finishes creation.
          pcall(function()
            if kind == 'TreeGroup' then
              pcall(function() if Attune_LoadTree then Attune_LoadTree() end end)
              -- Try to apply Attune's local tree if visible as a global (may be local in the addon chunk)
              if attunelocal_tree and type(attunelocal_tree) == 'table' and #attunelocal_tree > 0 then
                pcall(function() widget:SetTree(attunelocal_tree) end)
              else
                -- Fallback: attempt to synthesize a tree from Attune_Data (if present)
                pcall(function()
                  if Attune_Data and Attune_Data.attunes and type(Attune_Data.attunes) == 'table' then
                    local synth = {}
                    local expac = ''
                    local group = ''
                    local expacNode = nil
                    local groupNode = nil
                    for i,a in pairs(Attune_Data.attunes) do
                      if group ~= a.GROUP or expac ~= a.EXPAC then
                        if groupNode and expacNode then table.insert(expacNode.children, groupNode) end
                        if expac ~= a.EXPAC then
                          if expacNode then table.insert(synth, expacNode) end
                          expacNode = { value = a.EXPAC, text = a.EXPAC, children = {} }
                          expac = a.EXPAC
                        end
                        groupNode = { value = a.GROUP, text = tostring(a.GROUP), children = {} }
                        group = a.GROUP
                      end
                      if a.FACTION == UnitFactionGroup('player') or a.FACTION == 'Both' then
                        local text = a.NAME or tostring(a.ID)
                        local attuneNode = { value = a.ID, text = text }
                        table.insert(groupNode.children, attuneNode)
                      end
                    end
                    if groupNode and expacNode then table.insert(expacNode.children, groupNode) end
                    if expacNode then table.insert(synth, expacNode) end
                    if #synth > 0 then pcall(function() print('FLUX-DBG: applying synthesized tree count=' .. tostring(#synth)); widget:SetTree(synth) end) end
                  end
                end)
              end
            end
          end)
        return widget
      end
    function acegui:Create(kind, name)
      return makeWidget(kind, name)
    end
    LibStub._libs['AceGUI-3.0'] = acegui
    -- Watch for global assignments to `attunelocal_tree` and propagate to any existing TreeGroup widgets
    pcall(function()
      local gmt = getmetatable(_G) or {}
      local old_newindex = gmt.__newindex
      gmt.__newindex = function(t,k,v)
        if old_newindex then old_newindex(t,k,v) else rawset(t,k,v) end
        if k == 'attunelocal_tree' and type(v) == 'table' then
          pcall(function()
            for _, w in ipairs(acegui._widgets or {}) do
              if w and w._kind == 'TreeGroup' then pcall(function() w:SetTree(v) end) end
            end
          end)
        end
      end
      setmetatable(_G, gmt)
    end)
  end

  -- Minimal LibDataBroker stub
  do
    local ldb = {}
    ldb.objects = ldb.objects or {}
    function ldb:NewDataObject(name, data)
      self.objects[name] = data or {}
      return self.objects[name]
    end
    LibStub._libs['LibDataBroker-1.1'] = ldb
  end

  -- Minimal LibDBIcon stub
  do
    local ldbi = {}
    function ldbi:Show(name)
      pcall(function()
        Flux._minimap_buttons = Flux._minimap_buttons or {}
        if Flux._minimap_buttons[name] then Flux._minimap_buttons[name].visible = true end
        table.insert(Flux._host_calls, 'LibDBIconShow|' .. tostring(name))
      end)
    end
    function ldbi:Hide(name)
      pcall(function()
        Flux._minimap_buttons = Flux._minimap_buttons or {}
        if Flux._minimap_buttons[name] then Flux._minimap_buttons[name].visible = false end
        table.insert(Flux._host_calls, 'LibDBIconHide|' .. tostring(name))
      end)
    end
    function ldbi:Register(name, broker, db)
      pcall(function()
        Flux._minimap_buttons = Flux._minimap_buttons or {}
        -- store the broker table for host-side invocation; keep ref to broker and db
        Flux._minimap_buttons[name] = { broker = broker, db = db, visible = true }
        -- notify the host to create a visual button for this registration
        table.insert(Flux._host_calls, 'LibDBIconRegister|' .. tostring(name))
      end)
    end
    LibStub._libs['LibDBIcon-1.0'] = ldbi
  end

  -- Minimal LibDB storage (Attune may expect Attune_DB)
  Attune_DB = Attune_DB or { minimapbuttonpos = { hide = false }, autosurvey = false, showSurveyed = true, showResponses = true, showStepReached = true }

  -- Slash command table stub (SlashCmdList) used by many addons
  SlashCmdList = SlashCmdList or {}

local function join(...) local t={} for i=1,select('#',...) do t[i]=tostring(select(i,...)) end return table.concat(t,'\t') end
function print(...) io.write(join(...) .. '\n') end

function CreateFrame(ftype, name, parent)
  -- If caller provided a global name and a frame with that name already exists in _G,
  -- return the existing frame (matches WoW behavior where CreateFrame with a name
  -- returns the existing global frame rather than creating a new one).
  if name and name ~= '' and type(_G[name]) == 'table' then
    return _G[name]
  end

  -- Ensure frames have a usable name for serialization/lookup. If caller didn't provide
  -- a name, generate a stable-ish unique name so other frames can reference it.
  local fname = name
  if not fname or fname == '' then
    fname = tostring(ftype) .. '_' .. tostring(math.random(100000,999999))
  end

  local frame = { _type=ftype, _name=fname, _events = {}, scripts = {}, _points = {}, _children = {}, _shown = false }

  -- record creation for host (so the C# side can create a visual placeholder)
  pcall(function()
    table.insert(Flux._frames, frame)
    -- record a runtime tostring(frame) -> stable name map so the serializer can
    -- resolve stringified table identifiers ('table: 0x...') back to names.
    Flux._tostring_map = Flux._tostring_map or {}
    Flux._tostring_map[tostring(frame)] = fname
    local entry = { cmd = 'CreateFrame', name = tostring(name or ''), ftype = tostring(ftype or ''), parent = tostring((parent and parent._name) or '') }
    Flux._host_calls[#Flux._host_calls + 1] = entry
    print('FLUX-DBG: CreateFrame ' .. tostring(name or '') .. ' type=' .. tostring(ftype or ''))
  end)

  function frame:RegisterEvent(ev)
    self._events[ev]=true
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
    -- Keep the relativeTo value as-is (may be a frame table). Do NOT convert to string;
    -- serializer will extract _name from a table when possible. Instrument at runtime
    -- so we can detect code paths that pass stringified tables.
    pcall(function()
      local rtType = type(relativeTo)
      local rtStr = tostring(relativeTo)
      print('FLUX-DBG: SetPoint called frame=' .. tostring(self._name or '') .. ' point=' .. tostring(point) .. ' relType=' .. rtType .. ' relTo=' .. rtStr)
    end)
    table.insert(self._points, { point=point, relativeTo = relativeTo, relPoint=relPoint, x = x, y = y })
  end
  function frame:SetSize(w,h)
    self.width = w
    self.height = h
  end
  function frame:GetChildren()
    local _unpack = table.unpack or unpack
    if _unpack then return _unpack(self._children) end
    return nil
  end
  function frame:ClearAllPoints()
    self._points = {}
  end
  function frame:SetWidth(w) self.width = w end
  function frame:SetHeight(h) self.height = h end
  function frame:SetScale(s) self.scale = s end
  function frame:SetResizeBounds(minw,minh,maxw,maxh) self.resizeBounds = {minw=minw,minh=minh,maxw=maxw,maxh=maxh} end
  function frame:SetFrameStrata(s) self.strata = s end
  function frame:SetBackdrop(b) self.backdrop = b end
  function frame:SetBackdropColor(r,g,b,a) self.backdropColor = {r,g,b,a} end
  function frame:SetMovable(v) self.movable = v end
  function frame:SetClampedToScreen(v) self.clamped = v end
  function frame:EnableMouse(v) self.mouseEnabled = v end
  function frame:SetToplevel(v) self.toplevel = v end
  function frame:SetText(t) self._text = tostring(t) end
  function frame:Disable() self._disabled = true end
  function frame:Enable() self._disabled = false end
  function frame:CreateFontString(name)
    local fs = { _type = 'FontString', _name = tostring(name or 'fs_' .. tostring(math.random(1000,9999))), text = '', parent = self }
    self._fontstrings = self._fontstrings or {}
    table.insert(self._fontstrings, fs)
    table.insert(Flux._frames, fs)
    return fs
  end
  function frame:CreateTexture(name, layer)
    local tex = { _type = 'Texture', _name = tostring(name or 'tex_' .. tostring(math.random(1000,9999))), texturePath = '', parent = self, layer = layer, _points = {} }
    -- tex coords defaults
    tex.texcoords = { 0, 1, 0, 1 }
    function tex:SetTexture(path)
      self.texturePath = path
    end
    function tex:SetTexCoord(a,b,c,d,e,f,g,h)
      -- support SetTexCoord(u1, v1, u2, v2) or 4-arg and 8-arg forms
      if a and b and c and d and not e then
        self.texcoords = { a, b, c, d }
      elseif a and b and c and d and e and f and g and h then
        self.texcoords = { a, b, c, d, e, f, g, h }
      end
    end
    function tex:SetSize(w,h)
      self.width = w
      self.height = h
    end
    function tex:SetPoint(point, relativeTo, relPoint, x, y)
      -- support optional args
      point = point or ''
      -- Keep relativeTo as-is (it's a frame table, don't convert to string yet)
      relativeTo = relativeTo or ''
      relPoint = relPoint or ''
      x = x or 0
      y = y or 0
      pcall(function()
        local rtType = type(relativeTo)
        local rtStr = tostring(relativeTo)
        print('FLUX-DBG: Texture:SetPoint parent=' .. tostring(self.parent and self.parent._name or '') .. ' point=' .. tostring(point) .. ' relType=' .. rtType .. ' relTo=' .. rtStr)
      end)
      table.insert(self._points, { point=point, relativeTo=relativeTo, relPoint=relPoint, x=x, y=y })
    end
    -- attach to parent so serializer sees it
    self._textures = self._textures or {}
    table.insert(self._textures, tex)
    table.insert(Flux._frames, tex)
    return tex
  end
  function frame:CreateLine(name)
    local ln = { _type = 'Line', _name = tostring(name or 'line_' .. tostring(math.random(1000,9999))) }
    table.insert(Flux._frames, ln)
    return ln
  end

  -- parent/child relationship
  if parent ~= nil then
    table.insert(parent._children, frame)
    frame._parent = parent
  end

  -- expose named frames in the global table like WoW does
  if name ~= nil and type(name) == 'string' then
    _G[name] = frame
  end

  return frame
end

-- Minimal WoW constants and helpers
WOW_PROJECT_MAINLINE = WOW_PROJECT_MAINLINE or 1
WOW_PROJECT_CLASSIC = WOW_PROJECT_CLASSIC or 2
WOW_PROJECT_BURNING_CRUSADE_CLASSIC = WOW_PROJECT_BURNING_CRUSADE_CLASSIC or 5

function GetTime() return os.clock() end

-- Minimal WoW build info shim
function GetBuildInfo()
  -- version, build, date, tocVersion
  return '1.12.1', '3355', 'Dec 8 2025', '11200'
end

-- Minimal realm/player stubs
function GetRealmName()
  return 'LocalRealm'
end
function UnitName(unit)
  if unit == 'player' then return 'Player' end
  return tostring(unit)
end

-- Minimal guild info stub
function GetGuildInfo(unit)
  -- return guildName, guildRankName, guildRankIndex
  return nil, nil, nil
end

-- Minimal C_QuestLog shim
C_QuestLog = C_QuestLog or {}
function C_QuestLog.IsQuestFlaggedCompleted(...) return false end

-- Minimal font stubs
GameFontNormal = GameFontNormal or { GetFont = function() return 'Arial', 12, 'normal' end }
GameFontHighlight = GameFontHighlight or { GetFont = function() return 'Arial', 12, 'highlight' end }

function _DispatchEvent(ev, ...)
  -- If ADDON_LOADED, also call registered AceAddon lifecycle methods
  if ev == 'ADDON_LOADED' then
    local aname = select(1, ...)
    if LibStub and LibStub._libs and LibStub._libs['AceAddon-3.0'] and LibStub._libs['AceAddon-3.0']._addons then
      local a = LibStub._libs['AceAddon-3.0']._addons[aname]
      if a then
        pcall(function() if type(a.OnInitialize) == 'function' then a:OnInitialize() end end)
        pcall(function() if type(a.OnEnable) == 'function' then a:OnEnable() end end)
      end
    end
  end

  for i,frame in ipairs(Flux._frames) do
    if frame._events and frame._events[ev] then
      local f = frame.scripts and frame.scripts['OnEvent']
      if f then pcall(f, frame, ev, ...) end
    end
  end
end
";

            // Ensure KopiLua assemblies are probed/loaded (TryRun performs probing),
            // then install the Flux shim into the KopiLua VM using a file-based
            // invocation so that the native lua_State is created/cached for
            // subsequent file-based addon loads and the serializer.
            try
            {
              try { KopiLuaRunner.TryRun("-- probe"); } catch { }
              var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
              var shimPath = Path.Combine(baseDir, "flux_shim.lua");
              try { File.WriteAllText(shimPath, lua); } catch { /* best-effort */ }
              var (okShim, resShim) = KopiLuaRunner.TryRunFile(shimPath);
              Console.WriteLine($"Flux shim via TryRunFile: success={okShim}, res={(resShim ?? "(null)")}");
              if (!okShim)
              {
                // Fallback to TryRun if TryRunFile failed
                var (ok2, res2) = KopiLuaRunner.TryRun(lua);
                Console.WriteLine($"Flux shim via TryRun fallback TryRun: success={ok2}, res={(res2 ?? "(null)")}");
              }
            }
            catch
            {
              // ignore failures here; TryRun fallback will be attempted earlier
              try { KopiLuaRunner.TryRun(lua); } catch { }
            }
        }

        // Try to load an addon folder that contains a `main.lua` file.
        // Returns (success, framesSerialized) where framesSerialized is a ';' separated list of frame entries.
        public static (bool,string) TryLoadAddonDirectory(string addonPath)
        {
          try
          {
            // Ensure the Flux shim is installed into the VM before any addon files
            // are executed. This avoids initializer ordering where addons run
            // before the shim registers globals like WOW_PROJECT_CLASSIC.
            try { LoadApi(); } catch { }
            var mainFile = Path.Combine(addonPath, "main.lua");

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
            // Determine which Lua files to run. Prefer `main.lua`. If missing, try parsing a .toc file
            // or fall back to running all top-level .lua files in the folder.
            var filesToRun = new System.Collections.Generic.List<string>();
            if (File.Exists(mainFile))
            {
              filesToRun.Add(mainFile);
            }
            else
            {
              try
              {
                var tocFiles = Directory.GetFiles(addonPath, "*.toc", System.IO.SearchOption.TopDirectoryOnly);
                if (tocFiles.Length > 0)
                {
                  // parse first .toc for lua entries
                  var toc = File.ReadAllLines(tocFiles[0]);
                  foreach (var ln in toc)
                  {
                    var s = ln?.Trim();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.StartsWith("#") || s.StartsWith("##") || s.StartsWith("--")) continue;
                    // toc lines can contain locale/metadata; consider lines that end with .lua
                    var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var candidate = parts[0];
                    if (candidate.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                      var full = Path.Combine(addonPath, candidate.Replace('/', Path.DirectorySeparatorChar));
                      if (File.Exists(full)) filesToRun.Add(full);
                    }
                  }
                }
              }
              catch { }

              if (filesToRun.Count == 0)
              {
                try
                {
                  var luaFiles = Directory.GetFiles(addonPath, "*.lua", System.IO.SearchOption.TopDirectoryOnly);
                  foreach (var f in luaFiles) filesToRun.Add(f);
                }
                catch { }
              }
            }

            if (filesToRun.Count == 0)
            {
              Console.WriteLine($"No runnable lua files found in addon '{Path.GetFileName(addonPath)}'.");
              return (false, string.Empty);
            }

            // Attempt to load optional post-load fixer registrations from the
            // `fixers/` directory so callers can register per-addon fixers that
            // run after ADDON_LOADED. Files are loaded in filesystem order.
            try
            {
              var fixersDir = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "fixers");
              if (Directory.Exists(fixersDir))
              {
                var fx = Directory.GetFiles(fixersDir, "*.lua");
                foreach (var fpath in fx)
                {
                  try
                  {
                    var (okFix, resFix) = KopiLuaRunner.TryRunFile(fpath);
                    Console.WriteLine($"Loaded fixer {Path.GetFileName(fpath)}: success={okFix}, resLen={(resFix?.Length ?? 0)}");
                  }
                  catch { }
                }
              }
            }
            catch { }

            // Execute discovered files using a per-file wrapper that uses xpcall(debug.traceback)
            // to capture file-level tracebacks and ensure the code executes in the
            // native lua_State via TryRunFile. We always use the wrapper approach
            // so errors include stack traces and DoString-like helpers don't cause
            // the loader to receive chunk text instead of execution results.
            foreach (var f in filesToRun)
            {
              string wrapperPath = null!;
              try
              {
                Console.WriteLine($"Running addon file: {f}");
                wrapperPath = Path.Combine(Path.GetTempPath(), "flux_xpcall_" + Guid.NewGuid().ToString("N") + ".lua");
                var wrapperCode =
                  "local f, err = loadfile([===[" + f + "]===])" + Environment.NewLine +
                  "if not f then return tostring(err) end" + Environment.NewLine +
                  "local function run() return f() end" + Environment.NewLine +
                  "local ok, res = xpcall(run, debug.traceback)" + Environment.NewLine +
                  "if not ok then error(res) end" + Environment.NewLine +
                  "return res";
                File.WriteAllText(wrapperPath, wrapperCode);
                var (okWrapper, outWrapper) = KopiLuaRunner.TryRunFile(wrapperPath);
                var outSnippet = outWrapper;
                if (!string.IsNullOrEmpty(outSnippet) && outSnippet.Length > 500) outSnippet = outSnippet.Substring(0, 500) + "...";
                Console.WriteLine($"Ran file {Path.GetFileName(f)} via TryRunFile(xpcall wrapper): success={okWrapper}, outLen={(outWrapper?.Length ?? 0)}, out={outSnippet}");
                if (!okWrapper)
                {
                  // Last-resort fallback: try direct TryRun which may execute via DoString-like helpers
                  var (okText2, resText2) = KopiLuaRunner.TryRun(f);
                  Console.WriteLine($"Ran file {Path.GetFileName(f)} via TryRun(filename) fallback: success={okText2}, resLen={(resText2?.Length ?? 0)}, res={(resText2?.Length>500?resText2.Substring(0,500)+"...":resText2)}");
                }
              }
              catch (Exception ex)
              {
                Console.WriteLine($"Error running addon file '{f}': {ex.Message}");
                try
                {
                  var (okText, resText) = KopiLuaRunner.TryRun(f);
                  Console.WriteLine($"Ran file {Path.GetFileName(f)} via TryRun(filename) after error: success={okText}, resLen={(resText?.Length ?? 0)}");
                }
                catch { }
              }
              finally
              {
                try { if (!string.IsNullOrEmpty(wrapperPath) && File.Exists(wrapperPath)) File.Delete(wrapperPath); } catch { }
              }
            }

            // Diagnostic: query Flux._frames count immediately after addon load
            try
            {
              var diag = "local c = 0; if Flux and Flux._frames then c = #Flux._frames end; return tostring(c)";
              // Some KopiLua DoString-like helpers may return the chunk/source text
              // instead of executing it. Force execution via the C-API by writing
              // the snippet to a temporary file and invoking TryRunFile so the
              // native lua_State executes the file with luaL_loadfile/lua_pcall.
              var tmp = Path.Combine(Path.GetTempPath(), "flux_diag_" + Guid.NewGuid().ToString("N") + ".lua");
              try
              {
                File.WriteAllText(tmp, diag);
                var (dOkFile, dResFile) = KopiLuaRunner.TryRunFile(tmp);
                Console.WriteLine($"Diagnostic (file) : Flux._frames count (post-load): success={dOkFile}, value={dResFile}");
              }
              catch
              {
                // fallback to TryRun if TryRunFile is not available for some reason
                try
                {
                  var (dOk, dRes) = KopiLuaRunner.TryRun(diag);
                  Console.WriteLine($"Diagnostic (fallback): Flux._frames count (post-load): success={dOk}, value={dRes}");
                }
                catch (Exception ex3)
                {
                  Console.WriteLine($"Diagnostic error querying Flux._frames: {ex3.Message}");
                }
              }
              finally
              {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
              }
            }
            catch (Exception ex)
            {
              Console.WriteLine($"Diagnostic error querying Flux._frames: {ex.Message}");
            }

            // Simulate common WoW events so addons that initialize on events create frames
            try
            {
              var (evOk1, evRes1) = KopiLuaRunner.TryRun($"_DispatchEvent('ADDON_LOADED', '{Path.GetFileName(addonPath)}')");
              Console.WriteLine($"Dispatched ADDON_LOADED for '{Path.GetFileName(addonPath)}': success={evOk1}");
              var (evOk2, evRes2) = KopiLuaRunner.TryRun("_DispatchEvent('PLAYER_LOGIN')");
              Console.WriteLine($"Dispatched PLAYER_LOGIN: success={evOk2}");
              var (evOk3, evRes3) = KopiLuaRunner.TryRun("_DispatchEvent('PLAYER_ENTERING_WORLD')");
              Console.WriteLine($"Dispatched PLAYER_ENTERING_WORLD: success={evOk3}");
              try
              {
                var (pfOk, pfRes) = KopiLuaRunner.TryRun($"if Flux and Flux.ApplyPostLoadFixers then return tostring(Flux.ApplyPostLoadFixers('{Path.GetFileName(addonPath)}')) end");
                Console.WriteLine($"Applied post-load fixers for '{Path.GetFileName(addonPath)}': success={pfOk}, ran={pfRes}");
              }
              catch { }
            }
            catch (Exception ex)
            {
              Console.WriteLine($"Error dispatching simulated events: {ex.Message}");
            }

            // Special-case: if this addon appears to expose a global UI creation function, invoke it
            try
            {
              var addonName = Path.GetFileName(addonPath) ?? "";
              // Many addons create their AceGUI UI lazily; attempt to call a common pattern function
              // like <Addon>_Frame or Attune_Frame to force UI creation so serializer can see frames.
              var candidateFn = addonName + "_Frame";
              var invokeCode = $"if {candidateFn} then {candidateFn}() end";
              // Execute via a temporary file to force the C-API execution path.
              var tmpf = Path.Combine(Path.GetTempPath(), "flux_invoke_" + Guid.NewGuid().ToString("N") + ".lua");
                try
                {
                  File.WriteAllText(tmpf, invokeCode);
                  var (invOk, invOut) = KopiLuaRunner.TryRunFile(tmpf);
                  Console.WriteLine($"Invoked addon UI opener '{candidateFn}': success={invOk}, out={(invOut?.Length>200?invOut.Substring(0,200)+"...":invOut)}");
                }
              catch { }
              finally { try { if (File.Exists(tmpf)) File.Delete(tmpf); } catch { } }
            }
            catch { }

            // Development shortcut: prefer an addon-local `sample_frames.json` only.
            // Do not automatically use a global sample file in the app output directory,
            // as that may mask whether the addon actually produced frames at runtime.
            try
            {
              var localSamplePath = Path.Combine(addonPath, "sample_frames.json");
              if (File.Exists(localSamplePath))
              {
                var sampleContents = File.ReadAllText(localSamplePath);
                if (!string.IsNullOrWhiteSpace(sampleContents))
                {
                  Console.WriteLine($"Using addon-local sample_frames.json for addon '{Path.GetFileName(addonPath)}' (len={sampleContents.Length})");
                  return (true, sampleContents);
                }
              }
            }
            catch (Exception ex)
            {
              Console.WriteLine($"Error reading addon-local sample_frames.json: {ex.Message}");
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
                    return (false, string.Empty);
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

        // Return a copy of the expansion presets dictionary for the UI
        public static System.Collections.Generic.Dictionary<string, ExpansionPreset> GetExpansionPresets()
        {
            return new System.Collections.Generic.Dictionary<string, ExpansionPreset>(_expansionPresets, System.StringComparer.OrdinalIgnoreCase);
        }

        // Update or add an expansion preset (used by PresetEditorWindow)
        public static void UpdateExpansionPreset(string key, ExpansionPreset preset)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key) || preset == null) return;
                _expansionPresets[key] = preset;
            }
            catch { }
        }

        // Set the active expansion in the Lua VM by updating globals and GetBuildInfo
        public static void SetExpansion(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                if (!_expansionPresets.TryGetValue(name, out var p))
                {
                    // try simple variants
                    if (string.Equals(name, "Vanilla", StringComparison.OrdinalIgnoreCase)) p = _expansionPresets["vanilla"];
                    else if (string.Equals(name, "Mists", StringComparison.OrdinalIgnoreCase)) p = _expansionPresets["mists"];
                    else p = _expansionPresets["mainline"];
                }

                var ver = (p.Version ?? string.Empty).Replace("'", "\\'");
                var build = (p.Build ?? string.Empty).Replace("'", "\\'");
                var lua = $@"
WOW_PROJECT_ID = {p.WOW_PROJECT_ID}
WOW_PROJECT = '{p.Name}'
function GetBuildInfo()
  return '{ver}', '{build}', WOW_PROJECT_ID
end
";
                KopiLuaRunner.TryRun(lua);
            }
            catch { }
        }

        // Poll and clear queued host calls from the Flux shim. Returns newline-separated entries.
        public static string PollHostCalls()
        {
            try
            {
                var lua = @"
local out = {}
if Flux and Flux._host_calls then
  for i=1,#Flux._host_calls do
    local v = Flux._host_calls[i]
    if type(v) == 'table' then
      table.insert(out, tostring(v.cmd or '') .. '|' .. tostring(v.name or '') .. '|' .. tostring(v.ftype or '') .. '|' .. tostring(v.parent or '') .. '|' .. tostring(v.arg or ''))
    else
      table.insert(out, tostring(v))
    end
  end
  Flux._host_calls = {}
end
return table.concat(out, '\n')
";
                var (ok, res) = KopiLuaRunner.TryRun(lua);
                if (ok) return res ?? string.Empty;
                return res ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        // Execute a slash-style chat command (e.g. "/myaddon args") inside the Lua VM.
        // Returns true if a matching SlashCmdList handler was found and invoked.
        public static bool ExecuteSlashCommand(string input)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(input)) return false;
                // Build a small Lua script that looks up SLASH_* globals and SlashCmdList
                // and invokes the matching function with the remainder of the input.
                var lua = "local input = [===[" + input + "]===]\n"
                          + "local cmd, rest = input:match('^/([^%s]+)%s*(.*)$')\n"
                          + "if not cmd then return 'no_cmd' end\n"
                          + "local norm = '/'..cmd\n"
                          + "local lnorm = string.lower(norm)\n"
                          + "for k,v in pairs(_G) do\n"
                          + "  if type(k) == 'string' and string.sub(k,1,6) == 'SLASH_' then\n"
                          + "    local raw = string.sub(k,7)\n"
                          + "    -- strip trailing digits from SLASH_ keys (e.g., Attune1 -> Attune)\n"
                          + "    local key = string.match(raw, '^(.-)%d*$') or raw\n"
                          + "    local entries = v\n"
                          + "    if type(entries) == 'string' then\n"
                          + "      if string.lower(entries) == lnorm then\n"
                          + "        local fn = SlashCmdList[key] or SlashCmdList[raw]\n"
                          + "        if type(fn) == 'function' then pcall(fn, rest); return 'ok|'..key end\n"
                          + "      end\n"
                          + "    elseif type(entries) == 'table' then\n"
                          + "      for i=1,#entries do\n"
                          + "        if string.lower(entries[i] or '') == lnorm then\n"
                          + "          local fn = SlashCmdList[key] or SlashCmdList[raw]\n"
                          + "          if type(fn) == 'function' then pcall(fn, rest); return 'ok|'..key end\n"
                          + "        end\n"
                          + "      end\n"
                          + "    end\n"
                          + "  end\n"
                          + "end\n"
                          + "-- fallback: try several key casings and common variants\n"
                          + "local trykeys = {}\n"
                          + "table.insert(trykeys, string.upper(cmd))\n"
                          + "table.insert(trykeys, string.lower(cmd))\n"
                          + "local cap = string.upper(string.sub(cmd,1,1))..string.sub(cmd,2)\n"
                          + "table.insert(trykeys, cap)\n"
                          + "for _,tk in ipairs(trykeys) do\n"
                          + "  local fn = SlashCmdList[tk] or SlashCmdList[tk..'1'] or SlashCmdList[tk..'0']\n"
                          + "  if type(fn) == 'function' then pcall(fn, rest); return 'ok|'..tostring(tk) end\n"
                          + "end\n"
                          + "return 'not_found'\n";

                // Execute via a temp file to ensure the native VM path is used.
                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flux_slash_" + System.Guid.NewGuid().ToString("N") + ".lua");
                try
                {
                    System.IO.File.WriteAllText(tmp, lua);
                    var (ok, res) = KopiLuaRunner.TryRunFile(tmp);
                    Console.WriteLine($"ExecuteSlashCommand: success={ok}, res={(res ?? "(null)")}");
                    if (!string.IsNullOrWhiteSpace(res) && res.Trim().StartsWith("ok")) return true;
                    return false;
                }
                finally
                {
                    try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { }
                }
            }
            catch { return false; }
        }

        // Dump runtime diagnostics: SlashCmdList, SLASH_* globals, and Flux._minimap_buttons
        // Returns a newline-separated string suitable for appending to the emulator console.
        public static string DumpRuntimeDiagnostics()
        {
            try
            {
                var lua = @"local out = {}
-- Dump SLASH_ globals (mapping name->value(s))
for k,v in pairs(_G) do
  if type(k) == 'string' and string.sub(k,1,6) == 'SLASH_' then
    local key = string.sub(k,7)
    if type(v) == 'string' then
      table.insert(out, string.format('SLASH_%s = %s', key, tostring(v)))
    elseif type(v) == 'table' then
      for i=1,#v do table.insert(out, string.format('SLASH_%s[%d] = %s', key, i, tostring(v[i] or '')) ) end
    end
  end
end

-- Dump SlashCmdList handlers (show which keys map to functions)
if type(SlashCmdList) == 'table' then
  for k,v in pairs(SlashCmdList) do
    if type(v) == 'function' then
      table.insert(out, string.format('SlashCmdList[%s] = function', tostring(k)))
    else
      table.insert(out, string.format('SlashCmdList[%s] = %s', tostring(k), tostring(v)))
    end
  end
else
  table.insert(out, 'SlashCmdList = (not a table)')
end

-- Dump Flux._minimap_buttons registry
if Flux and Flux._minimap_buttons then
  for name,rec in pairs(Flux._minimap_buttons) do
    local icon = '(nil)'
    local pos = '(nil)'
    pcall(function()
      if rec and rec.broker and rec.broker.icon then icon = tostring(rec.broker.icon) end
      if rec and rec.db and (rec.db.minimapPos or rec.db.minimapbuttonpos) then pos = tostring(rec.db.minimapPos or rec.db.minimapbuttonpos) end
    end)
    table.insert(out, string.format('MinimapButton:%s icon=%s pos=%s visible=%s', tostring(name), icon, pos, tostring(rec.visible)))
  end
else
  table.insert(out, 'Flux._minimap_buttons = (empty)')
end

return table.concat(out, '\n')";

                var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flux_diag_" + System.Guid.NewGuid().ToString("N") + ".lua");
                try
                {
                    System.IO.File.WriteAllText(tmp, lua);
                    var (ok, res) = KopiLuaRunner.TryRunFile(tmp);
                    if (ok) return res ?? string.Empty;
                    return res ?? string.Empty;
                }
                finally { try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { } }
            }
            catch { return string.Empty; }
        }

        // Scan LibDataBroker objects and return newline-separated lines of the form:
        // LDB|<name>|<type>|<hasOnClick>|<icon>
        public static string ScanLibDataBroker()
        {
            try
            {
                var lua = @"local out = {}
local ldb = nil
if LibStub and LibStub._libs and LibStub._libs['LibDataBroker-1.1'] then ldb = LibStub._libs['LibDataBroker-1.1'].objects end
if not ldb and ldb_objects then ldb = ldb_objects end
ldb = ldb or {}
for name, data in pairs(ldb) do
  local t = tostring((data and data.type) or '')
  local hasOnClick = 0
  pcall(function() if data and type(data.OnClick) == 'function' then hasOnClick = 1 end end)
  local icon = ''
  pcall(function()
    if data then
      if data.icon and type(data.icon) == 'string' then icon = data.icon end
      if icon == '' and data.iconfile and type(data.iconfile) == 'string' then icon = data.iconfile end
      if icon == '' and data.iconTexture and type(data.iconTexture) == 'string' then icon = data.iconTexture end
      if icon == '' and data.texture and type(data.texture) == 'string' then icon = data.texture end
      if icon == '' and type(data.icon) == 'function' then icon = '(func)' end
    end
  end)
  table.insert(out, string.format('LDB|%s|%s|%d|%s', tostring(name), t, hasOnClick, tostring(icon)))
end
return table.concat(out, '\n')";

                var (ok, res) = KopiLuaRunner.TryRun(lua);
                if (ok && !string.IsNullOrEmpty(res)) return res; else return string.Empty;
            }
            catch { return string.Empty; }
        }

        // Attempt to invoke common addon UI opener functions after load. Returns newline-separated results.
        public static string InvokeAddonUiOpeners(string addonName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(addonName)) return string.Empty;
                var safe = addonName.Replace("\"", "\\\"");
                var lua = $@"local out = {{}}
local name = '{safe}'
local cand = {{ name..'_Show', name..'_ShowUI', name..'_Toggle', name..'_Frame', name..'Frame', name..'_Open', name..'_OpenUI', name }}
for i,fnname in ipairs(cand) do
  local f = _G[fnname]
  if type(f) == 'function' then
    local ok, err = pcall(f)
    table.insert(out, string.format('Invoked function %s -> %s', fnname, ok and 'ok' or tostring(err)))
    return table.concat(out, '\n')
  end
  -- if a frame table exists with a Show method, call it
  local frame = _G[fnname]
  if type(frame) == 'table' and type(frame.Show) == 'function' then
    local ok, err = pcall(frame.Show, frame)
    table.insert(out, string.format('Invoked frame.Show on %s -> %s', fnname, ok and 'ok' or tostring(err)))
    return table.concat(out, '\n')
  end
end
table.insert(out, 'No opener found')
return table.concat(out, '\n')";

                var (ok, res) = KopiLuaRunner.TryRun(lua);
                if (ok && !string.IsNullOrEmpty(res)) return res; else return res ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
