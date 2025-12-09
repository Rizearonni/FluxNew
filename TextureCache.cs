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

                // Fast path for common image types supported by ImageSharp
                var ext = Path.GetExtension(norm)?.ToLowerInvariant() ?? string.Empty;

                if (ext != ".blp")
                {
                    using var img = Image.Load<Rgba32>(norm);
                    var w = img.Width;
                    var h = img.Height;
                    var buf = new byte[w * h * 4];
                    img.CopyPixelDataTo(buf);
                    var td = new TextureData { Width = w, Height = h, Rgba = buf, Path = norm };
                    s_cache[norm] = td;
                    return td;
                }

                // Fallback: basic BLP handling. Many BLP2 files contain an embedded JPEG stream,
                // but many others use DXT (BCn) compression. Try JPEG extraction first, then
                // attempt DXT decoding of mip0.
                var blpBytes = File.ReadAllBytes(norm);
                var jpegTried = false;
                if (TryExtractEmbeddedJpeg(blpBytes, out var jpegBytes))
                {
                    jpegTried = true;
                    try
                    {
                        using var img = Image.Load<Rgba32>(jpegBytes);
                        var w = img.Width;
                        var h = img.Height;
                        var buf = new byte[w * h * 4];
                        img.CopyPixelDataTo(buf);
                        var td = new TextureData { Width = w, Height = h, Rgba = buf, Path = norm };
                        s_cache[norm] = td;
                        Console.WriteLine($"TextureCache: decoded .blp (jpeg) '{norm}' -> {w}x{h}");
                        return td;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"TextureCache: failed to decode embedded JPEG from '{norm}': {ex.Message}");
                    }
                }

                // Try DXT decode (DXT1/DXT5) for compressed BLP files
                if (TryDecodeBlpDxt(blpBytes, out var rgbaBytes, out var dxtW, out var dxtH))
                {
                    try
                    {
                        var td2 = new TextureData { Width = dxtW, Height = dxtH, Rgba = rgbaBytes, Path = norm };
                        s_cache[norm] = td2;
                        Console.WriteLine($"TextureCache: decoded .blp (dxt) '{norm}' -> {dxtW}x{dxtH}");
                        return td2;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"TextureCache: failed to create texture from DXT decode for '{norm}': {ex.Message}");
                    }
                }

                if (!jpegTried)
                {
                    Console.WriteLine($"TextureCache: no embedded JPEG found and DXT decode failed for .blp '{norm}'");
                }
                else
                {
                    Console.WriteLine($"TextureCache: attempted JPEG extraction and DXT decode failed for .blp '{norm}'");
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TextureCache: failed to load '{path}': {ex.Message}");
                return null;
            }
        }

        // Try to decode DXT-compressed mip 0 from a BLP2 file into RGBA bytes.
        private static bool TryDecodeBlpDxt(byte[] blpBytes, out byte[] rgba, out int width, out int height)
        {
            rgba = Array.Empty<byte>();
            width = 0;
            height = 0;
            if (blpBytes == null || blpBytes.Length < 64) return false;
            try
            {
                var magic = System.Text.Encoding.ASCII.GetString(blpBytes, 0, Math.Min(4, blpBytes.Length));
                if (!string.Equals(magic, "BLP2", StringComparison.OrdinalIgnoreCase)) return false;
                uint type = BitConverter.ToUInt32(blpBytes, 4);
                // read width/height
                width = (int)BitConverter.ToUInt32(blpBytes, 0x0C);
                height = (int)BitConverter.ToUInt32(blpBytes, 0x10);
                var offsets = new uint[16];
                var sizes = new uint[16];
                if (blpBytes.Length >= 0x54 + 16 * 4)
                {
                    for (int i = 0; i < 16; i++) offsets[i] = BitConverter.ToUInt32(blpBytes, 0x14 + i * 4);
                    for (int i = 0; i < 16; i++) sizes[i] = BitConverter.ToUInt32(blpBytes, 0x54 + i * 4);
                }
                var off = (int)offsets[0];
                var sz = (int)sizes[0];
                if (off <= 0 || sz <= 0 || off + sz > blpBytes.Length) return false;
                var comp = new byte[sz];
                Array.Copy(blpBytes, off, comp, 0, sz);

                int blocksX = (width + 3) / 4;
                int blocksY = (height + 3) / 4;
                var expectedDxt1 = blocksX * blocksY * 8;
                var expectedDxt5 = blocksX * blocksY * 16;

                if (sz == expectedDxt1)
                {
                    rgba = DxtDecoder.DecodeDxt1(width, height, comp);
                    return true;
                }
                if (sz == expectedDxt5)
                {
                    rgba = DxtDecoder.DecodeDxt5(width, height, comp);
                    return true;
                }

                // Heuristic: if size is multiple of expectedDxt1 or Dxt5, try to pick the closest match
                if (sz % expectedDxt1 == 0)
                {
                    rgba = DxtDecoder.DecodeDxt1(width, height, comp);
                    return true;
                }
                if (sz % expectedDxt5 == 0)
                {
                    rgba = DxtDecoder.DecodeDxt5(width, height, comp);
                    return true;
                }

                // Last resort: try DXT5 then DXT1
                try { rgba = DxtDecoder.DecodeDxt5(width, height, comp); return true; } catch { }
                try { rgba = DxtDecoder.DecodeDxt1(width, height, comp); return true; } catch { }
            }
            catch { }
            return false;
        }

        private static class DxtDecoder
        {
            private struct Color32 { public byte R, G, B, A; }

            private static Color32 RgbFrom565(ushort c)
            {
                int r = (c >> 11) & 0x1F;
                int g = (c >> 5) & 0x3F;
                int b = c & 0x1F;
                byte rb = (byte)((r * 255 + 15) / 31);
                byte gb = (byte)((g * 255 + 31) / 63);
                byte bb = (byte)((b * 255 + 15) / 31);
                return new Color32 { R = rb, G = gb, B = bb, A = 255 };
            }

            public static byte[] DecodeDxt1(int width, int height, byte[] data)
            {
                int blocksX = (width + 3) / 4;
                int blocksY = (height + 3) / 4;
                var outBuf = new byte[width * height * 4];
                int srcOff = 0;
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        if (srcOff + 8 > data.Length) break;
                        ushort c0 = BitConverter.ToUInt16(data, srcOff);
                        ushort c1 = BitConverter.ToUInt16(data, srcOff + 2);
                        uint code = BitConverter.ToUInt32(data, srcOff + 4);
                        srcOff += 8;
                        var cols = new Color32[4];
                        cols[0] = RgbFrom565(c0);
                        cols[1] = RgbFrom565(c1);
                        if (c0 > c1)
                        {
                            cols[2] = new Color32 {
                                R = (byte)((2 * cols[0].R + cols[1].R) / 3),
                                G = (byte)((2 * cols[0].G + cols[1].G) / 3),
                                B = (byte)((2 * cols[0].B + cols[1].B) / 3),
                                A = 255
                            };
                            cols[3] = new Color32 {
                                R = (byte)((cols[0].R + 2 * cols[1].R) / 3),
                                G = (byte)((cols[0].G + 2 * cols[1].G) / 3),
                                B = (byte)((cols[0].B + 2 * cols[1].B) / 3),
                                A = 255
                            };
                        }
                        else
                        {
                            cols[2] = new Color32 {
                                R = (byte)((cols[0].R + cols[1].R) / 2),
                                G = (byte)((cols[0].G + cols[1].G) / 2),
                                B = (byte)((cols[0].B + cols[1].B) / 2),
                                A = 255
                            };
                            cols[3] = new Color32 { R = 0, G = 0, B = 0, A = 0 };
                        }

                        for (int py = 0; py < 4; py++)
                        {
                            for (int px = 0; px < 4; px++)
                            {
                                int idx = (int)(code & 0x3);
                                code >>= 2;
                                int x = bx * 4 + px;
                                int y = by * 4 + py;
                                if (x >= width || y >= height) continue;
                                var c = cols[idx];
                                var pos = (y * width + x) * 4;
                                outBuf[pos] = c.R;
                                outBuf[pos + 1] = c.G;
                                outBuf[pos + 2] = c.B;
                                outBuf[pos + 3] = c.A;
                            }
                        }
                    }
                }
                return outBuf;
            }

            public static byte[] DecodeDxt5(int width, int height, byte[] data)
            {
                int blocksX = (width + 3) / 4;
                int blocksY = (height + 3) / 4;
                var outBuf = new byte[width * height * 4];
                int srcOff = 0;
                for (int by = 0; by < blocksY; by++)
                {
                    for (int bx = 0; bx < blocksX; bx++)
                    {
                        if (srcOff + 16 > data.Length) break;
                        byte a0 = data[srcOff];
                        byte a1 = data[srcOff + 1];
                        ulong alphaBits = 0;
                        // next 6 bytes are alpha indices (little-endian)
                        for (int i = 0; i < 6; i++) alphaBits |= ((ulong)data[srcOff + 2 + i]) << (8 * i);
                        srcOff += 8;
                        ushort c0 = BitConverter.ToUInt16(data, srcOff);
                        ushort c1 = BitConverter.ToUInt16(data, srcOff + 2);
                        uint code = BitConverter.ToUInt32(data, srcOff + 4);
                        srcOff += 8;

                        // build alpha palette
                        byte[] alpha = new byte[8];
                        alpha[0] = a0;
                        alpha[1] = a1;
                        if (a0 > a1)
                        {
                            for (int i = 1; i <= 6; i++)
                            {
                                alpha[i + 1] = (byte)(((7 - i) * a0 + i * a1 + 3) / 7);
                            }
                        }
                        else
                        {
                            for (int i = 1; i <= 4; i++)
                            {
                                alpha[i + 1] = (byte)(((5 - i) * a0 + i * a1 + 2) / 5);
                            }
                            alpha[6] = 0;
                            alpha[7] = 255;
                        }

                        var cols = new Color32[4];
                        cols[0] = RgbFrom565(c0);
                        cols[1] = RgbFrom565(c1);
                        if (c0 > c1)
                        {
                            cols[2] = new Color32 {
                                R = (byte)((2 * cols[0].R + cols[1].R) / 3),
                                G = (byte)((2 * cols[0].G + cols[1].G) / 3),
                                B = (byte)((2 * cols[0].B + cols[1].B) / 3),
                                A = 255
                            };
                            cols[3] = new Color32 {
                                R = (byte)((cols[0].R + 2 * cols[1].R) / 3),
                                G = (byte)((cols[0].G + 2 * cols[1].G) / 3),
                                B = (byte)((cols[0].B + 2 * cols[1].B) / 3),
                                A = 255
                            };
                        }
                        else
                        {
                            cols[2] = new Color32 {
                                R = (byte)((cols[0].R + cols[1].R) / 2),
                                G = (byte)((cols[0].G + cols[1].G) / 2),
                                B = (byte)((cols[0].B + cols[1].B) / 2),
                                A = 255
                            };
                            cols[3] = new Color32 { R = 0, G = 0, B = 0, A = 0 };
                        }

                        for (int py = 0; py < 4; py++)
                        {
                            for (int px = 0; px < 4; px++)
                            {
                                int pixelIndex = py * 4 + px;
                                int alphaIndex = (int)((alphaBits >> (3 * pixelIndex)) & 0x7);
                                byte aval = alpha[alphaIndex];

                                int colorIndex = (int)(code & 0x3);
                                code >>= 2;
                                int x = bx * 4 + px;
                                int y = by * 4 + py;
                                if (x >= width || y >= height) continue;
                                var c = cols[colorIndex];
                                var pos = (y * width + x) * 4;
                                outBuf[pos] = c.R;
                                outBuf[pos + 1] = c.G;
                                outBuf[pos + 2] = c.B;
                                outBuf[pos + 3] = aval;
                            }
                        }
                    }
                }
                return outBuf;
            }
        }

        private static bool TryExtractEmbeddedJpeg(byte[] blpBytes, out byte[] jpegBytes)
        {
            jpegBytes = Array.Empty<byte>();
            if (blpBytes == null || blpBytes.Length < 16) return false;

            // Quick BLP2 header parse: many BLP2 files embed a JPEG mipmap with offsets.
            try
            {
                if (blpBytes.Length >= 4)
                {
                    var magic = System.Text.Encoding.ASCII.GetString(blpBytes, 0, 4);
                    if (string.Equals(magic, "BLP2", StringComparison.OrdinalIgnoreCase))
                    {
                        // Layout (common variant):
                        // 0x00: 'BLP2' (4)
                        // 0x04: uint32 type (1 = JPEG, 2 = compressed/paletted)
                        // 0x08: uint32 flags
                        // 0x0C: uint32 width
                        // 0x10: uint32 height
                        // 0x14: uint32 mipOffsets[16]
                        // 0x54: uint32 mipSizes[16]
                        if (blpBytes.Length >= 0x54 + 16 * 4)
                        {
                            uint type = BitConverter.ToUInt32(blpBytes, 4);
                            uint width = BitConverter.ToUInt32(blpBytes, 0x0C);
                            uint height = BitConverter.ToUInt32(blpBytes, 0x10);
                            var offsets = new uint[16];
                            var sizes = new uint[16];
                            for (int i = 0; i < 16; i++) offsets[i] = BitConverter.ToUInt32(blpBytes, 0x14 + i * 4);
                            for (int i = 0; i < 16; i++) sizes[i] = BitConverter.ToUInt32(blpBytes, 0x54 + i * 4);

                            if (type == 1)
                            {
                                // JPEG-compressed color mipmaps.
                                var off = (int)offsets[0];
                                var sz = (int)sizes[0];
                                // Many BLP2 files store a JPEG header blob after the sizes table.
                                // At offset 0x94 there is often a uint32 jpegHeaderSize followed by header bytes.
                                try
                                {
                                    var headerPos = 0x94;
                                    if (blpBytes.Length >= headerPos + 4)
                                    {
                                        var jpegHeaderSize = (int)BitConverter.ToUInt32(blpBytes, headerPos);
                                        var jpegHeaderStart = headerPos + 4;
                                        if (jpegHeaderSize > 0 && jpegHeaderStart + jpegHeaderSize <= blpBytes.Length)
                                        {
                                            var header = new byte[jpegHeaderSize];
                                            Array.Copy(blpBytes, jpegHeaderStart, header, 0, jpegHeaderSize);
                                            if (off > 0 && sz > 0 && off + sz <= blpBytes.Length)
                                            {
                                                jpegBytes = new byte[jpegHeaderSize + sz];
                                                Array.Copy(header, 0, jpegBytes, 0, jpegHeaderSize);
                                                Array.Copy(blpBytes, off, jpegBytes, jpegHeaderSize, sz);
                                                return true;
                                            }
                                        }
                                    }
                                }
                                catch { }
                                // Fallback: raw mip data
                                if (off > 0 && sz > 0 && off + sz <= blpBytes.Length)
                                {
                                    jpegBytes = new byte[sz];
                                    Array.Copy(blpBytes, off, jpegBytes, 0, sz);
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { /* fall through to marker scan */ }

            // Search for JPEG start marker 0xFF 0xD8 and end marker 0xFF 0xD9.
            int bestStart = -1;
            int bestEnd = -1;
            for (int i = 0; i + 1 < blpBytes.Length; i++)
            {
                if (blpBytes[i] == 0xFF && blpBytes[i + 1] == 0xD8)
                {
                    // Found start; find nearest end after it
                    for (int j = i + 2; j + 1 < blpBytes.Length; j++)
                    {
                        if (blpBytes[j] == 0xFF && blpBytes[j + 1] == 0xD9)
                        {
                            // choose the largest segment (prefer later starts with later ends)
                            if (bestStart < 0 || (j + 1 - i) > (bestEnd - bestStart))
                            {
                                bestStart = i;
                                bestEnd = j + 1; // inclusive index of 0xD9
                            }
                            break;
                        }
                    }
                }
            }

            if (bestStart >= 0 && bestEnd > bestStart)
            {
                var len = bestEnd - bestStart + 1;
                // defensive: clamp len
                if (bestStart + len > blpBytes.Length) len = blpBytes.Length - bestStart;
                jpegBytes = new byte[len];
                Array.Copy(blpBytes, bestStart, jpegBytes, 0, len);
                return true;
            }

            return false;
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
