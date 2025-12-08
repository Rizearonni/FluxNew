-- Simple test addon that creates frames with textures

local frame = CreateFrame("Frame", "TestTextureFrame")
frame:SetSize(400, 300)
frame:SetPoint("CENTER", UIParent, "CENTER", 0, 0)

-- Create a background texture using numeric ID (checkbox)
local bgTexture = frame:CreateTexture("TestBgTexture", "BACKGROUND")
bgTexture:SetTexture(130755) -- UI-CheckBox-Up.tga
bgTexture:SetSize(32, 32)
bgTexture:SetPoint("TOPLEFT", frame, "TOPLEFT", 10, -10)

-- Create a border texture using path
local borderTexture = frame:CreateTexture("TestBorderTexture", "ARTWORK")
borderTexture:SetTexture("Interface\\DialogFrame\\UI-DialogBox-Gold-Border")
borderTexture:SetSize(256, 128)
borderTexture:SetPoint("CENTER", frame, "CENTER", 0, 0)

-- Create another texture with texcoords
local iconTexture = frame:CreateTexture("TestIconTexture", "OVERLAY")
iconTexture:SetTexture("Interface\\Icons\\INV_Misc_QuestionMark")
iconTexture:SetSize(64, 64)
iconTexture:SetPoint("BOTTOMRIGHT", frame, "BOTTOMRIGHT", -10, 10)
iconTexture:SetTexCoord(0.1, 0.9, 0.1, 0.9)

print("TestAddon: Created frame with 3 textures")
