using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FluxNew
{
    public class FrameData
    {
        public string name { get; set; } = "";
        public string type { get; set; } = "";
        public bool shown { get; set; }
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
        public double scale { get; set; } = 1.0;
        public string? texturePath { get; set; }
        public string? layer { get; set; }
        public double[]? texcoords { get; set; }
        public string? text { get; set; }
        public string? parent { get; set; }
        public string? parentNodePath { get; set; }
        public string? nodePath { get; set; }
        public List<object>? anchors { get; set; }
        public List<string>? children { get; set; }
    }

    public static class FrameRenderer
    {
        /// <summary>
        /// Load frames from debug_frames.json and render textures onto a Canvas (preserves existing content)
        /// </summary>
        public static void RenderFramesToCanvas(Canvas canvas, string? addonDirectory = null)
        {
            // DON'T clear the canvas - we want to add textures on top of existing frames
            // Remove any previously rendered textures/frames from this renderer
            var toRemove = canvas.Children
                .Where(c => c is Image || (c is Border b && b.Tag?.ToString() == "FrameRenderer"))
                .ToList();
            foreach (var child in toRemove)
            {
                canvas.Children.Remove(child);
            }

            // Try multiple locations for debug_frames.json
            var jsonPath = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "debug_frames.json");
            if (!File.Exists(jsonPath))
            {
                // Try project root (parent of bin/Debug/net8.0)
                var projectRoot = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "..", "..", "..");
                jsonPath = Path.Combine(projectRoot, "debug_frames.json");
                if (!File.Exists(jsonPath))
                {
                    // Try current directory
                    jsonPath = Path.Combine(Environment.CurrentDirectory, "debug_frames.json");
                    if (!File.Exists(jsonPath))
                    {
                        Console.WriteLine($"FrameRenderer: debug_frames.json not found (searched BaseDirectory, ProjectRoot, and CurrentDirectory)");
                        return;
                    }
                }
            }
            
            Console.WriteLine($"FrameRenderer: Loading frames from {jsonPath}");

            try
            {
                var json = File.ReadAllText(jsonPath);
                var frames = JsonSerializer.Deserialize<List<FrameData>>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (frames == null || frames.Count == 0)
                {
                    Console.WriteLine("FrameRenderer: No frames in JSON");
                    return;
                }

                Console.WriteLine($"FrameRenderer: Loaded {frames.Count} frames from JSON");

                // Build a simple rendering pass: textures first, then frames, then fontstrings
                var textures = new List<FrameData>();
                var regularFrames = new List<FrameData>();
                var fontStrings = new List<FrameData>();

                foreach (var frame in frames)
                {
                    if (frame.type == "Texture") textures.Add(frame);
                    else if (frame.type == "FontString") fontStrings.Add(frame);
                    else regularFrames.Add(frame);
                }

                // Render textures
                foreach (var tex in textures)
                {
                    if (string.IsNullOrWhiteSpace(tex.texturePath)) continue;

                    var resolvedPath = AssetPathResolver.Resolve(tex.texturePath, addonDirectory);
                    if (resolvedPath == null)
                    {
                        Console.WriteLine($"FrameRenderer: Could not resolve texture path '{tex.texturePath}'");
                        continue;
                    }

                    var textureData = TextureCache.LoadTexture(resolvedPath);
                    if (textureData == null)
                    {
                        Console.WriteLine($"FrameRenderer: Failed to load texture from '{resolvedPath}'");
                        continue;
                    }

                    var bitmap = CreateBitmapFromRgba(textureData);
                    if (bitmap == null) continue;

                    var image = new Image
                    {
                        Source = bitmap,
                        Width = tex.w > 0 ? tex.w : textureData.Width,
                        Height = tex.h > 0 ? tex.h : textureData.Height,
                        Tag = "FrameRenderer"
                    };

                    Canvas.SetLeft(image, tex.x);
                    Canvas.SetTop(image, tex.y);
                    canvas.Children.Add(image);

                    Console.WriteLine($"FrameRenderer: Rendered texture '{tex.name}' at ({tex.x},{tex.y}) size ({image.Width}x{image.Height})");
                }

                // Render regular frames as placeholders (simple borders)
                foreach (var frame in regularFrames)
                {
                    if (!frame.shown) continue;
                    if (frame.w <= 0 || frame.h <= 0) continue;

                    var border = new Border
                    {
                        Width = frame.w,
                        Height = frame.h,
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(1),
                        Background = new SolidColorBrush(Color.FromArgb(30, 100, 100, 100)),
                        Tag = "FrameRenderer"
                    };

                    Canvas.SetLeft(border, frame.x);
                    Canvas.SetTop(border, frame.y);
                    canvas.Children.Add(border);
                }

                // Render fontstrings as TextBlocks
                foreach (var fs in fontStrings)
                {
                    if (string.IsNullOrWhiteSpace(fs.text)) continue;

                    var textBlock = new TextBlock
                    {
                        Text = fs.text,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        Tag = "FrameRenderer"
                    };

                    Canvas.SetLeft(textBlock, fs.x);
                    Canvas.SetTop(textBlock, fs.y);
                    canvas.Children.Add(textBlock);

                    Console.WriteLine($"FrameRenderer: Rendered text '{fs.text}' at ({fs.x},{fs.y})");
                }

                Console.WriteLine($"FrameRenderer: Render complete - {textures.Count} textures, {regularFrames.Count} frames, {fontStrings.Count} labels");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FrameRenderer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert TextureData (RGBA byte array) to Avalonia WriteableBitmap
        /// </summary>
        private static WriteableBitmap? CreateBitmapFromRgba(TextureData data)
        {
            try
            {
                var bitmap = new WriteableBitmap(
                    new PixelSize(data.Width, data.Height),
                    new Vector(96, 96),
                    PixelFormat.Rgba8888,
                    AlphaFormat.Unpremul
                );

                using (var buffer = bitmap.Lock())
                {
                    unsafe
                    {
                        var ptr = (byte*)buffer.Address;
                        var stride = buffer.RowBytes;

                        for (int y = 0; y < data.Height; y++)
                        {
                            for (int x = 0; x < data.Width; x++)
                            {
                                var srcIdx = (y * data.Width + x) * 4;
                                var dstIdx = y * stride + x * 4;

                                ptr[dstIdx + 0] = data.Rgba[srcIdx + 0]; // R
                                ptr[dstIdx + 1] = data.Rgba[srcIdx + 1]; // G
                                ptr[dstIdx + 2] = data.Rgba[srcIdx + 2]; // B
                                ptr[dstIdx + 3] = data.Rgba[srcIdx + 3]; // A
                            }
                        }
                    }
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CreateBitmapFromRgba error: {ex.Message}");
                return null;
            }
        }
    }
}
