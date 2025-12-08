using System;
using System.Collections.Concurrent;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FluxNew
{
    public class TextureData
    {
        public int Width { get; set; }
        public int Height { get; set; }
        // RGBA32 bytes (Width * Height * 4)
        public byte[] Rgba { get; set; } = Array.Empty<byte>();
        public string Path { get; set; } = string.Empty;
    }

    public static class TextureCache
    {
        private static ConcurrentDictionary<string, TextureData> s_cache = new ConcurrentDictionary<string, TextureData>(StringComparer.OrdinalIgnoreCase);

        public static TextureData? LoadTexture(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var norm = NormalizePath(path);
            if (s_cache.TryGetValue(norm, out var existing)) return existing;

            try
            {
                if (!File.Exists(norm)) return null;
                using var img = Image.Load<Rgba32>(norm);
                var w = img.Width;
                var h = img.Height;
                var buf = new byte[w * h * 4];
                img.CopyPixelDataTo(buf);
                var td = new TextureData { Width = w, Height = h, Rgba = buf, Path = norm };
                s_cache[norm] = td;
                return td;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TextureCache: failed to load '{path}': {ex.Message}");
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            // If path is a numeric texture id, or contains backslashes from WoW style, try to convert
            // Common WoW texture references include 'Interface\\DialogFrame\\UI-DialogBox-Header' or numeric ids.
            var p = path ?? string.Empty;
            // Trim surrounding quotes
            p = p.Trim('"', '\'', ' ');
            // Replace double backslashes
            p = p.Replace("\\\\", "\\");
            // If path contains Interface\, try to resolve against a common WoW Interface folder in Program working dir
            if (p.IndexOf("Interface\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                var candidates = new[] { Path.Combine(baseDir, p), Path.Combine(baseDir, "..", "Interface", p) };
                foreach (var c in candidates)
                {
                    try
                    {
                        var f = Path.GetFullPath(c);
                        if (File.Exists(f)) return f;
                    }
                    catch { }
                }
            }
            // Fallback: treat as relative path from current directory
            try
            {
                var fp = Path.GetFullPath(p);
                return fp;
            }
            catch { return p; }
        }
    }
}
