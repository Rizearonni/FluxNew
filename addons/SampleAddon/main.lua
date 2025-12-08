-- SampleAddon for Flux emulator
print("SampleAddon.lua: loaded")

local f = CreateFrame("Frame", "SampleFrame")
f:SetScript("OnEvent", function(self, event, ...)
  print("SampleAddon: event received", event)
end)
f:RegisterEvent("PLAYER_LOGIN")

-- simulate a login event for testing
_DispatchEvent("PLAYER_LOGIN")
