-- Flux minimal WoW API shim
Flux = Flux or {}
Flux._frames = Flux._frames or {}
Flux._host_calls = Flux._host_calls or {}
-- Post-load fixer registry: allow external scripts to register functions
-- that will run after an addon is loaded so they can patch or synthesize
-- data (useful for addons that keep data in local upvalues or populate
-- their trees lazily). Fixers receive the addon name as a parameter.
Flux._post_load_fixers = Flux._post_load_fixers or {}
function Flux.RegisterPostLoadFixer(name, fn)
  if type(fn) ~= 'function' then return false end
  table.insert(Flux._post_load_fixers, { name = tostring(name or ''), fn = fn })
  return true
end
function Flux.ApplyPostLoadFixers(addonName)
  local okCount = 0
  for i,rec in ipairs(Flux._post_load_fixers or {}) do
    pcall(function()
      rec.fn(addonName)
      okCount = okCount + 1
    end)
  end
  return okCount
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
    function ldbi:Show(name) end
    function ldbi:Hide(name) end
    function ldbi:Register(name, broker, db) end
    LibStub._libs['LibDBIcon-1.0'] = ldbi
  end

  -- Minimal LibDB storage (Attune may expect Attune_DB)
  Attune_DB = Attune_DB or { minimapbuttonpos = { hide = false }, autosurvey = false, showSurveyed = true, showResponses = true, showStepReached = true }

  -- Slash command table stub (SlashCmdList) used by many addons
  SlashCmdList = SlashCmdList or {}

local function join(...) local t={} for i=1,select('#',...) do t[i]=tostring(select(i,...)) end return table.concat(t,'\t') end
function print(...) io.write(join(...) .. '\n') end

function CreateFrame(ftype, name, parent)
  local frame = { _type=ftype, _name=name, _events = {}, scripts = {}, _points = {}, _children = {}, _shown = false }

  -- record creation for host (so the C# side can create a visual placeholder)
  pcall(function()
    table.insert(Flux._frames, frame)
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
    table.insert(self._points, { point=point, relativeTo = tostring(relativeTo), relPoint=relPoint, x = x, y = y })
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
