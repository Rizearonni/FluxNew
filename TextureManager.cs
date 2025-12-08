using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace FluxNew
{
    public static class TextureManager
    {
        private static readonly object s_lock = new object();
        private static JsonDocument? s_index;
        private static string? s_root;

        private static void EnsureLoaded()
        {
            if (s_index != null) return;
            lock (s_lock)
            {
                if (s_index != null) return;
                var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                var candidates = new[] {
                    Path.Combine(baseDir, "assets", "textures"),
                    Path.Combine(baseDir, "textures"),
                    Path.Combine(baseDir, "wow-ui-textures"),
                    Path.Combine(baseDir, "assets")
                };
                s_root = null;
                foreach (var c in candidates)
                {
                    try { if (Directory.Exists(c)) { s_root = c; break; } } catch { }
                }

                if (!string.IsNullOrEmpty(s_root))
                {
                    var idxPath = Path.Combine(s_root, "index.json");
                    if (File.Exists(idxPath))
                    {
                        try
                        {
                            var txt = File.ReadAllText(idxPath);
                            s_index = JsonDocument.Parse(txt);
                        }
                        catch { s_index = null; }
                    }
                }
            }
        }

        public static string? GetPath(string section, string key)
        {
            try
            {
                EnsureLoaded();
                if (s_index == null || string.IsNullOrEmpty(s_root)) return null;
                if (!s_index.RootElement.TryGetProperty(section, out var sec)) return null;
                if (!sec.TryGetProperty(key, out var val)) return null;
                var rel = val.GetString();
                if (string.IsNullOrEmpty(rel)) return null;
                var full = Path.Combine(s_root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) return full;
            }
            catch { }
            return null;
        }

        public static Bitmap? GetBitmap(string section, string key)
        {
            try
            {
                var p = GetPath(section, key);
                if (string.IsNullOrEmpty(p)) return null;
                return new Bitmap(p);
            }
            catch { return null; }
        }

        public static string? GetModuleIconPath(string moduleName)
        {
            try
            {
                EnsureLoaded();
                if (s_index == null) return null;
                if (s_index.RootElement.TryGetProperty("moduleIcons", out var mi))
                {
                    // try exact key
                    if (mi.TryGetProperty(moduleName, out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        var rel = val.GetString();
                        if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(s_root))
                        {
                            var full = Path.Combine(s_root, rel.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(full)) return full;
                        }
                    }
                    // try case-insensitive search
                    foreach (var prop in mi.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            var rel = prop.Value.GetString();
                            if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(s_root))
                            {
                                var full = Path.Combine(s_root, rel.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(full)) return full;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static string? GetPortraitPath(string unitName)
        {
            try
            {
                EnsureLoaded();
                if (s_index == null) return null;
                if (s_index.RootElement.TryGetProperty("characterframe", out var cf))
                {
                    // try player portrait
                    if (unitName != null && unitName.Equals("player", StringComparison.OrdinalIgnoreCase))
                    {
                        if (cf.TryGetProperty("player_portrait", out var pp) && pp.ValueKind == JsonValueKind.String)
                        {
                            var rel = pp.GetString();
                            if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(s_root))
                            {
                                var full = Path.Combine(s_root, rel.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(full)) return full;
                            }
                        }
                    }

                    // try direct portrait matches
                    if (cf.TryGetProperty("portraits", out var portraits) && portraits.ValueKind == JsonValueKind.Object)
                    {
                        if (!string.IsNullOrEmpty(unitName) && portraits.TryGetProperty(unitName, out var found) && found.ValueKind == JsonValueKind.String)
                        {
                            var rel = found.GetString();
                            if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(s_root))
                            {
                                var full = Path.Combine(s_root, rel.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(full)) return full;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
