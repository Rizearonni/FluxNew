using System;
using System.Collections.Generic;
using System.IO;

namespace FluxNew
{
    public static class AssetPathResolver
    {
        // Numeric texture ID to file path mappings (common WoW textures)
        private static readonly Dictionary<string, string> s_textureIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Common UI textures referenced by numeric IDs
            { "130755", "Interface/Buttons/UI-CheckBox-Up.tga" },
            { "130751", "Interface/Buttons/UI-CheckBox-Check.tga" },
            { "130753", "Interface/Buttons/UI-CheckBox-Highlight.tga" },
            { "130843", "Interface/Buttons/UI-RadioButton.tga" },
            { "131080", "Interface/DialogFrame/UI-DialogBox-Header.tga" },
            { "137057", "Interface/Tooltips/UI-Tooltip-Border.tga" },
            { "136430", "Interface/Minimap/MiniMap-TrackingBorder.tga" },
            { "136467", "Interface/Minimap/UI-Minimap-Background.tga" },
            { "136580", "Interface/PaperDollInfoFrame/UI-Character-Tab-Highlight.tga" },
        };

        // Search paths for asset resolution (relative to app base directory)
        private static readonly string[] s_searchPaths = new[]
        {
            "", // current directory
            "Interface",
            "assets",
            "assets/Interface",
            "../Interface", // parent directory
        };

        /// <summary>
        /// Resolve a WoW-style texture path or numeric ID to an actual file path.
        /// Returns null if not found.
        /// </summary>
        public static string? Resolve(string texturePath, string? addonDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(texturePath)) return null;

            var path = texturePath.Trim().Trim('"', '\'');
            
            // Check if it's a numeric ID
            if (s_textureIdMap.TryGetValue(path, out var mappedPath))
            {
                path = mappedPath;
            }

            // Normalize separators (WoW uses backslash)
            path = path.Replace("\\\\", "/").Replace("\\", "/");

            var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
            var extensions = new[] { "", ".tga", ".blp", ".png", ".jpg" };

            // 1. Try addon directory if provided
            if (!string.IsNullOrEmpty(addonDirectory))
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(addonDirectory, path + ext);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }

            // 2. Try search paths relative to base directory
            foreach (var searchPath in s_searchPaths)
            {
                var searchDir = string.IsNullOrEmpty(searchPath) 
                    ? baseDir 
                    : Path.Combine(baseDir, searchPath);

                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(searchDir, path + ext);
                    try
                    {
                        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    }
                    catch { /* invalid path, continue */ }
                }
            }

            // 3. Try as absolute or relative path from current directory
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = path + ext;
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Register additional texture ID mappings at runtime
        /// </summary>
        public static void RegisterTextureId(string id, string path)
        {
            s_textureIdMap[id] = path;
        }
    }
}
