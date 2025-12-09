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
using Avalonia.Threading;

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
            // Remove any previously rendered elements produced by this renderer (only those tagged by us)
            var toRemove = canvas.Children
                .Where(c => (c is Control ctrl) && ctrl.Tag?.ToString()?.StartsWith("FrameRenderer:") == true)
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

                // Build name -> frame map for quick lookups
                var frameByName = frames.Where(f => !string.IsNullOrEmpty(f.name)).ToDictionary(f => f.name, f => f);

                // Build dependency graph (frame -> frames it depends on) by inspecting anchors/parent refs
                var deps = new Dictionary<string, HashSet<string>>();
                var rev = new Dictionary<string, HashSet<string>>();
                foreach (var f in frameByName.Values)
                {
                    deps[f.name] = new HashSet<string>();
                    rev[f.name] = new HashSet<string>();
                }

                foreach (var f in frameByName.Values)
                {
                    // If the frame has an explicit parent, prefer that as a dependency
                    if (!string.IsNullOrEmpty(f.parent) && frameByName.ContainsKey(f.parent))
                    {
                        deps[f.name].Add(f.parent);
                        rev[f.parent].Add(f.name);
                    }

                    if (f.anchors != null)
                    {
                        foreach (var a in f.anchors)
                        {
                            try
                            {
                                var ad = NormalizeAnchor(a);
                                if (ad == null) continue;
                                if (ad.TryGetValue("relativeTo", out var relToRaw) && !string.IsNullOrEmpty(relToRaw))
                                {
                                    // numeric relativeTo values are shorthand offsets; ignore those
                                    if (!double.TryParse(relToRaw, out _))
                                    {
                                        if (frameByName.ContainsKey(relToRaw))
                                        {
                                            deps[f.name].Add(relToRaw);
                                            rev[relToRaw].Add(f.name);
                                        }
                                    }
                                }
                            }
                            catch { /* ignore malformed anchors */ }
                        }
                    }
                }

                // Perform topological sort (Kahn) to get parent-first resolution order
                var inDegree = new Dictionary<string, int>();
                foreach (var kv in deps) inDegree[kv.Key] = kv.Value.Count;

                var zero = new List<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
                zero.Sort(StringComparer.Ordinal);
                var topo = new List<string>();

                while (zero.Count > 0)
                {
                    var n = zero[0];
                    zero.RemoveAt(0);
                    topo.Add(n);

                    foreach (var m in rev[n])
                    {
                        inDegree[m] = inDegree[m] - 1;
                        if (inDegree[m] == 0) zero.Add(m);
                    }
                    zero.Sort(StringComparer.Ordinal);
                }

                if (topo.Count != frameByName.Count)
                {
                    // cycle detected; fall back to original unordered resolution but log diagnostic
                    Console.WriteLine($"FrameRenderer: Topological sort detected cycle (topo={topo.Count} of {frameByName.Count}). Falling back to unordered resolve.");
                    // Print a brief diagnostic of nodes still with incoming edges
                    var remaining = inDegree.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(20).ToList();
                    Console.WriteLine($"FrameRenderer: Nodes with remaining in-degree (sample {remaining.Count}): {string.Join(", ", remaining.Select(kv => kv.Key + "(" + kv.Value + ")"))}");
                }

                // Print topo summary for diagnostics
                Console.WriteLine($"FrameRenderer: Dependency graph built: nodes={frameByName.Count} edges={deps.Sum(kv=>kv.Value.Count)} roots={inDegree.Count(kv=>kv.Value==0)}");
                if (topo.Count > 0)
                {
                    Console.WriteLine($"FrameRenderer: Topo order sample (first 20): {string.Join(", ", topo.Take(20))}");
                }

                // Resolve positions for frames that have anchors but no explicit x/y
                var resolved = new HashSet<string>();

                (double x, double y, double w, double h) ResolveFrame(string name, int depth)
                {
                    if (depth > 20) return (0, 0, 0, 0);
                    if (string.IsNullOrEmpty(name)) return (0, 0, 0, 0);
                    if (resolved.Contains(name) && frameByName.TryGetValue(name, out var rf)) return (rf.x, rf.y, rf.w, rf.h);
                    if (!frameByName.TryGetValue(name, out var frameToResolve)) return (0, 0, 0, 0);

                    // If the frame already has non-zero geometry, accept it
                    if (frameToResolve.w > 0 || frameToResolve.h > 0 || frameToResolve.x != 0 || frameToResolve.y != 0)
                    {
                        resolved.Add(name);
                        return (frameToResolve.x, frameToResolve.y, frameToResolve.w, frameToResolve.h);
                    }

                    // Try anchors in order — prefer the first anchor that resolves cleanly
                    if (frameToResolve.anchors != null && frameToResolve.anchors.Count > 0)
                    {
                        foreach (var anchorObj in frameToResolve.anchors)
                        {
                            var anchorDict = NormalizeAnchor(anchorObj);
                            if (anchorDict == null) continue;

                            anchorDict.TryGetValue("relativeTo", out var relToRaw);
                            anchorDict.TryGetValue("relPoint", out var relPointRaw);
                            anchorDict.TryGetValue("point", out var pointRaw);
                            anchorDict.TryGetValue("x", out var xRaw);
                            anchorDict.TryGetValue("y", out var yRaw);

                            double offsetX = 0, offsetY = 0;
                            double.TryParse(xRaw ?? "0", out offsetX);
                            double.TryParse(yRaw ?? "0", out offsetY);

                            // If relativeTo is numeric, treat it as absolute offset from origin (legacy addon shorthand)
                            if (!string.IsNullOrEmpty(relToRaw) && double.TryParse(relToRaw, out var numRelTo))
                            {
                                frameToResolve.x = numRelTo + offsetX;
                                if (!string.IsNullOrEmpty(relPointRaw) && double.TryParse(relPointRaw, out var numRelPoint))
                                    frameToResolve.y = numRelPoint + offsetY;
                                else
                                    frameToResolve.y = offsetY;

                                resolved.Add(name);
                                return (frameToResolve.x, frameToResolve.y, frameToResolve.w, frameToResolve.h);
                            }

                            // Resolve target frame (relativeTo may be a name or omitted -> use parent)
                            var targetName = relToRaw ?? frameToResolve.parent ?? "";
                            var targetPos = ResolveFrame(targetName, depth + 1);

                            double tX = targetPos.x, tY = targetPos.y, tW = targetPos.w, tH = targetPos.h;

                            // Compute target anchor point using relPoint (defaults to TOPLEFT)
                            double anchorTargetX = tX, anchorTargetY = tY;
                            var relPoint = (relPointRaw ?? "TOPLEFT").ToUpper();
                            switch (relPoint)
                            {
                                case "TOPLEFT":
                                    anchorTargetX = tX; anchorTargetY = tY; break;
                                case "TOPRIGHT":
                                    anchorTargetX = tX + tW; anchorTargetY = tY; break;
                                case "BOTTOMLEFT":
                                    anchorTargetX = tX; anchorTargetY = tY + tH; break;
                                case "BOTTOMRIGHT":
                                    anchorTargetX = tX + tW; anchorTargetY = tY + tH; break;
                                case "CENTER":
                                    anchorTargetX = tX + tW / 2; anchorTargetY = tY + tH / 2; break;
                                case "LEFT":
                                    anchorTargetX = tX; anchorTargetY = tY + tH / 2; break;
                                case "RIGHT":
                                    anchorTargetX = tX + tW; anchorTargetY = tY + tH / 2; break;
                                case "TOP":
                                    anchorTargetX = tX + tW / 2; anchorTargetY = tY; break;
                                case "BOTTOM":
                                    anchorTargetX = tX + tW / 2; anchorTargetY = tY + tH; break;
                            }

                            // Determine this-frame anchor point and compute final position
                            var point = (pointRaw ?? "TOPLEFT").ToUpper();
                            double finalX = anchorTargetX, finalY = anchorTargetY;
                            switch (point)
                            {
                                case "TOPLEFT":
                                    finalX = anchorTargetX + offsetX;
                                    finalY = anchorTargetY + offsetY;
                                    break;
                                case "TOPRIGHT":
                                    finalX = anchorTargetX - frameToResolve.w + offsetX;
                                    finalY = anchorTargetY + offsetY;
                                    break;
                                case "BOTTOMRIGHT":
                                    finalX = anchorTargetX - frameToResolve.w + offsetX;
                                    finalY = anchorTargetY - frameToResolve.h + offsetY;
                                    break;
                                case "BOTTOMLEFT":
                                    finalX = anchorTargetX + offsetX;
                                    finalY = anchorTargetY - frameToResolve.h + offsetY;
                                    break;
                                case "CENTER":
                                    finalX = anchorTargetX - (frameToResolve.w / 2) + offsetX;
                                    finalY = anchorTargetY - (frameToResolve.h / 2) + offsetY;
                                    break;
                                case "LEFT":
                                    finalX = anchorTargetX + offsetX;
                                    finalY = anchorTargetY - (frameToResolve.h / 2) + offsetY;
                                    break;
                                case "RIGHT":
                                    finalX = anchorTargetX - frameToResolve.w + offsetX;
                                    finalY = anchorTargetY - (frameToResolve.h / 2) + offsetY;
                                    break;
                                case "TOP":
                                    finalX = anchorTargetX - (frameToResolve.w / 2) + offsetX;
                                    finalY = anchorTargetY + offsetY;
                                    break;
                                case "BOTTOM":
                                    finalX = anchorTargetX - (frameToResolve.w / 2) + offsetX;
                                    finalY = anchorTargetY - frameToResolve.h + offsetY;
                                    break;
                                default:
                                    finalX = anchorTargetX + offsetX;
                                    finalY = anchorTargetY + offsetY;
                                    break;
                            }

                            frameToResolve.x = finalX;
                            frameToResolve.y = finalY;
                            resolved.Add(name);
                            return (frameToResolve.x, frameToResolve.y, frameToResolve.w, frameToResolve.h);
                        }
                    }

                    // Nothing to do: mark resolved to avoid repeated work
                    resolved.Add(name);
                    return (frameToResolve.x, frameToResolve.y, frameToResolve.w, frameToResolve.h);
                }

                // Run resolution for all named frames using parent-first order when available
                if (topo.Count == frameByName.Count)
                {
                    foreach (var nm in topo)
                    {
                        ResolveFrame(nm, 0);
                    }
                }
                else
                {
                    foreach (var nm in frameByName.Keys.ToList()) ResolveFrame(nm, 0);
                }

                // Multi-pass: infer parent sizes from children and re-resolve positions.
                for (int pass = 0; pass < 4; pass++)
                {
                    bool changed = false;
                    foreach (var frame in frameByName.Values)
                    {
                        if (frame.children == null || frame.children.Count == 0) continue;

                        double maxChildW = 0;
                        double totalChildH = 0;
                        int childCount = 0;
                        // simple heuristics: if this frame is a TreeGroup or contains node children,
                        // lay out children vertically with a fixed row height and indent.
                        var isTreeGroup = !string.IsNullOrEmpty(frame.name) && frame.name.StartsWith("TreeGroup_");
                        var localYOffset = 0.0;
                        var rowHeight = 18.0;
                        var indent = 18.0; // space for icon + padding
                        foreach (var cname in frame.children)
                        {
                            if (frameByName.TryGetValue(cname, out var child))
                            {
                                // Apply some defaults for common child types
                                if (child.w <= 0)
                                {
                                    if (string.Equals(child.type, "Button", StringComparison.OrdinalIgnoreCase) || child.name?.StartsWith("Button_") == true)
                                    {
                                        child.w = 80; child.h = child.h <= 0 ? 20 : child.h;
                                    }
                                    else if (string.Equals(child.type, "Texture", StringComparison.OrdinalIgnoreCase) && child.w <= 0)
                                    {
                                        child.w = child.w <= 0 ? 16 : child.w; child.h = child.h <= 0 ? 16 : child.h;
                                    }
                                }

                                    // Tree node heuristic: detect node frames (nodePath assigned or name contains _node_)
                                    var isNode = !string.IsNullOrEmpty(child.nodePath) || (child.name?.Contains("_node_") == true);
                                    if ((isTreeGroup || isNode) )
                                    {
                                        // ensure row height
                                        if (child.h <= 0) child.h = rowHeight;

                                        // if parent width known, give child a reasonable width
                                        if (child.w <= 0 && frame.w > 0) child.w = Math.Max(80, frame.w - (int)indent - 12);

                                        // position child stacked vertically relative to parent
                                        // only set x/y if parent has non-zero position (otherwise defer)
                                        if (frame.x != 0 || frame.y != 0)
                                        {
                                            var oldX = child.x; var oldY = child.y;
                                            child.x = frame.x + indent;
                                            child.y = frame.y + localYOffset + 4; // small padding
                                            if (child.x != oldX || child.y != oldY) changed = true;
                                        }

                                        localYOffset += child.h + 2; // small spacing between rows
                                    }

                                    if (child.w > 0) maxChildW = Math.Max(maxChildW, child.w);
                                    if (child.h > 0) totalChildH += child.h;
                                    childCount++;
                            }
                        }

                        if (childCount == 0) continue;

                        var padding = 8;
                        var spacing = 4 * Math.Max(0, childCount - 1);

                        if ((frame.w <= 0 && maxChildW > 0) || (frame.h <= 0 && totalChildH > 0))
                        {
                            if (frame.w <= 0 && maxChildW > 0)
                            {
                                frame.w = maxChildW + padding;
                                changed = true;
                            }
                            if (frame.h <= 0 && totalChildH > 0)
                            {
                                frame.h = totalChildH + spacing + padding;
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                    {
                        // re-resolve positions now that sizes changed
                        foreach (var nm in frameByName.Keys.ToList()) ResolveFrame(nm, 0);
                    }
                    else break;
                }

                // Emit a small trace for the first few frames to help debugging layout
                int traceLimit = 12;
                foreach (var f in frameByName.Values.Take(traceLimit))
                {
                    Console.WriteLine($"FrameRenderer-TRACE: {f.name} -> x={f.x} y={f.y} w={f.w} h={f.h} anchors={(f.anchors?.Count ?? 0)} children={(f.children?.Count ?? 0)}");
                }

                // Build a simple rendering pass: textures first, then frames, then fontstrings
                var framesSnapshot = frames.ToList();

                // Helper: normalize an anchor object (JsonElement or Dictionary) into string->string map
                Dictionary<string, string>? NormalizeAnchor(object? anchorObj)
                {
                    if (anchorObj == null) return null;

                    // If it's already a dictionary of string->object, convert values to strings
                    if (anchorObj is System.Collections.Generic.Dictionary<string, object> dictObj)
                    {
                        var outd = new Dictionary<string, string>();
                        foreach (var kv in dictObj)
                            outd[kv.Key] = kv.Value?.ToString() ?? "";
                        return outd;
                    }

                    // If it's a JsonElement (most common when deserialized), extract properties
                    if (anchorObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var outd = new Dictionary<string, string>();
                        foreach (var prop in je.EnumerateObject())
                        {
                            // strip quotes if present
                            var raw = prop.Value.GetRawText();
                            outd[prop.Name] = raw.Trim('"');
                        }
                        return outd;
                    }

                    // Fallback: try to stringify
                    return new Dictionary<string, string> { ["toString"] = anchorObj.ToString() ?? "" };
                }

                Dispatcher.UIThread.Post(() =>
                {
                    var textures = new List<FrameData>();
                    var regularFrames = new List<FrameData>();
                    var fontStrings = new List<FrameData>();

                    foreach (var frame in framesSnapshot)
                    {
                        if (frame.type == "Texture") textures.Add(frame);
                        else if (frame.type == "FontString") fontStrings.Add(frame);
                        else regularFrames.Add(frame);
                    }

                    // Remove previous renderer children
                    var toRemoveLocal = canvas.Children
                        .Where(c => (c is Control ctrl) && ctrl.Tag?.ToString()?.StartsWith("FrameRenderer:") == true)
                        .ToList();
                    foreach (var child in toRemoveLocal)
                        canvas.Children.Remove(child);

                    // Populate sensible defaults for common types when sizes are missing
                    foreach (var f in regularFrames)
                    {
                        if (f.w <= 0)
                        {
                            if (string.Equals(f.type, "Button", StringComparison.OrdinalIgnoreCase) || f.name?.StartsWith("Button_") == true)
                                f.w = 80;
                            else if (string.Equals(f.type, "InlineGroup", StringComparison.OrdinalIgnoreCase) || f.name?.Contains("InlineGroup") == true)
                                f.w = f.w <= 0 ? 160 : f.w;
                            else if (string.Equals(f.type, "ScrollFrame", StringComparison.OrdinalIgnoreCase) || f.name?.Contains("ScrollFrame") == true)
                                f.w = 200;
                        }
                        if (f.h <= 0)
                        {
                            if (string.Equals(f.type, "Button", StringComparison.OrdinalIgnoreCase) || f.name?.StartsWith("Button_") == true)
                                f.h = 20;
                            else if (string.Equals(f.type, "InlineGroup", StringComparison.OrdinalIgnoreCase))
                                f.h = f.h <= 0 ? 24 : f.h;
                            else if (string.Equals(f.type, "ScrollFrame", StringComparison.OrdinalIgnoreCase) || f.name?.Contains("ScrollFrame") == true)
                                f.h = 200;
                        }
                        // Ensure a minimal visible footprint so debug rendering shows something
                        if (f.w <= 0) f.w = Math.Max(24, f.w);
                        if (f.h <= 0) f.h = Math.Max(24, f.h);
                    }

                    // Compute bounding box of all to-be-rendered items so we can translate them
                    // into the visible canvas area if the addon uses coordinates outside the viewport.
                    var allFrames = framesSnapshot.Where(ff => ff != null).ToList();
                    double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                    foreach (var ff in allFrames)
                    {
                        var lx = ff.x;
                        var ty = ff.y;
                        var rw = ff.w > 0 ? ff.w : 0;
                        var rh = ff.h > 0 ? ff.h : 0;
                        if (!double.IsNaN(lx) && !double.IsInfinity(lx)) minX = Math.Min(minX, lx);
                        if (!double.IsNaN(ty) && !double.IsInfinity(ty)) minY = Math.Min(minY, ty);
                        minX = Math.Min(minX, lx);
                        minY = Math.Min(minY, ty);
                        maxX = Math.Max(maxX, lx + rw);
                        maxY = Math.Max(maxY, ty + rh);
                    }

                    // If nothing meaningful, ensure defaults
                    if (double.IsPositiveInfinity(minX)) minX = 0;
                    if (double.IsPositiveInfinity(minY)) minY = 0;
                    if (double.IsNegativeInfinity(maxX)) maxX = canvas.Bounds.Width;
                    if (double.IsNegativeInfinity(maxY)) maxY = canvas.Bounds.Height;

                    // Desired margin from canvas edges
                    const double margin = 8.0;
                    double transX = 0, transY = 0;
                    // If items are positioned off-canvas (negative) move them in
                    if (minX < margin) transX = margin - minX;
                    if (minY < margin) transY = margin - minY;
                    // If items extend beyond canvas, try to nudge them left/up when possible
                    if (canvas.Bounds.Width > 0 && maxX + transX > canvas.Bounds.Width - margin)
                        transX = Math.Min(transX, (canvas.Bounds.Width - margin) - maxX);
                    if (canvas.Bounds.Height > 0 && maxY + transY > canvas.Bounds.Height - margin)
                        transY = Math.Min(transY, (canvas.Bounds.Height - margin) - maxY);

                    // Final clamp to avoid huge translations
                    if (double.IsNaN(transX) || double.IsInfinity(transX)) transX = 0;
                    if (double.IsNaN(transY) || double.IsInfinity(transY)) transY = 0;

                    // Helper to test whether an item lies (even partially) inside the canvas bounds
                    // Require full containment inside the visible canvas area. We don't allow
                    // partial or full overflow because addon frames should not render outside
                    // the emulation region (they were previously drawing over the sidebar).
                    bool IntersectsCanvas(double lx, double ty, double w, double h)
                    {
                        if (double.IsNaN(lx) || double.IsNaN(ty) || double.IsNaN(w) || double.IsNaN(h)) return false;
                        var right = lx + w;
                        var bottom = ty + h;
                        if (double.IsNaN(canvas.Bounds.Width) || double.IsNaN(canvas.Bounds.Height)) return false;
                        // require fully inside [0, width] x [0, height]
                        if (lx < 0) return false;
                        if (ty < 0) return false;
                        if (right > canvas.Bounds.Width) return false;
                        if (bottom > canvas.Bounds.Height) return false;
                        return true;
                    }

                    // Render regular frames as placeholders (simple borders)
                    foreach (var frame in regularFrames)
                    {
                        // Render regardless of reported 'shown' state so we can debug layout
                        if (frame.w <= 0 || frame.h <= 0) continue;

                        var left = frame.x + transX;
                        var top = frame.y + transY;

                        // Skip frames that lie fully outside the emulator canvas —
                        // addon code should not be able to create UI outside the emulation region.
                        if (!IntersectsCanvas(left, top, frame.w, frame.h))
                        {
                            // Optionally log a debug line for skip events
                            // Console.WriteLine($"FrameRenderer: Skipping off-canvas frame '{frame.name}' at ({left},{top}) size ({frame.w}x{frame.h})");
                            continue;
                        }

                        var border = new Border
                        {
                            Width = frame.w,
                            Height = frame.h,
                            BorderBrush = Brushes.Gray,
                            BorderThickness = new Thickness(1),
                            Background = new SolidColorBrush(Color.FromArgb(30, 100, 100, 100)),
                            Tag = $"FrameRenderer:{frame.name}"
                        };

                        Canvas.SetLeft(border, left);
                        Canvas.SetTop(border, top);
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
                            Tag = $"FrameRenderer:{fs.name}"
                        };

                        var tx = fs.x + transX;
                        var ty = fs.y + transY;

                        // Estimate textbounds when explicit w/h not provided
                        double tw = fs.w > 0 ? fs.w : Math.Max(40, (fs.text?.Length ?? 0) * 6);
                        double th = fs.h > 0 ? fs.h : 16;

                        if (!IntersectsCanvas(tx, ty, tw, th))
                        {
                            // Console.WriteLine($"FrameRenderer: Skipping off-canvas text '{fs.text}' at ({tx},{ty})");
                        }
                        else
                        {
                            Canvas.SetLeft(textBlock, tx);
                            Canvas.SetTop(textBlock, ty);
                            canvas.Children.Add(textBlock);
                        }

                        Console.WriteLine($"FrameRenderer: Rendered text '{fs.text}' at ({fs.x},{fs.y})");
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
                            Tag = $"FrameRenderer:{tex.name}"
                        };

                        // Calculate position from anchors if available
                        double posX = tex.x;
                        double posY = tex.y;
                        
                        if (tex.anchors != null && tex.anchors.Count > 0)
                        {
                            var anchor = tex.anchors[0]; // Use first anchor

                            // Try to find an already-rendered parent control (by tag)
                            Control? parentControl = canvas.Children
                                .OfType<Control>()
                                .FirstOrDefault(c => c.Tag?.ToString() == $"FrameRenderer:{tex.parent}");

                            // Fallback to frame data if control not found
                            var parentFrame = framesSnapshot.FirstOrDefault(f => f.name == tex.parent);

                            var anchorDict = NormalizeAnchor(anchor);
                            if (anchorDict != null)
                            {
                                anchorDict.TryGetValue("point", out var point);
                                anchorDict.TryGetValue("x", out var xval);
                                anchorDict.TryGetValue("y", out var yval);
                                double.TryParse(xval ?? "0", out var offsetX);
                                double.TryParse(yval ?? "0", out var offsetY);

                                double pX = 0, pY = 0, pW = 0, pH = 0;
                                if (parentControl != null)
                                {
                                    pX = Canvas.GetLeft(parentControl);
                                    pY = Canvas.GetTop(parentControl);
                                    pW = parentControl.Width;
                                    pH = parentControl.Height;
                                }
                                else if (parentFrame != null)
                                {
                                    pX = parentFrame.x;
                                    pY = parentFrame.y;
                                    pW = parentFrame.w;
                                    pH = parentFrame.h;
                                }

                                switch (point?.ToUpper())
                                {
                                    case "TOPLEFT":
                                        posX = pX + offsetX;
                                        posY = pY + offsetY;
                                        break;
                                    case "CENTER":
                                        posX = pX + (pW / 2) - (image.Width / 2) + offsetX;
                                        posY = pY + (pH / 2) - (image.Height / 2) + offsetY;
                                        break;
                                    case "BOTTOMRIGHT":
                                        posX = pX + pW - image.Width + offsetX;
                                        posY = pY + pH - image.Height + offsetY;
                                        break;
                                    default:
                                        posX = pX + offsetX;
                                        posY = pY + offsetY;
                                        break;
                                }
                            }
                        }

                        var imgLeft = posX + transX;
                        var imgTop = posY + transY;

                        if (!IntersectsCanvas(imgLeft, imgTop, image.Width, image.Height))
                        {
                            // Console.WriteLine($"FrameRenderer: Skipping off-canvas texture '{tex.name}' at ({imgLeft},{imgTop})");
                            continue;
                        }

                        Canvas.SetLeft(image, imgLeft);
                        Canvas.SetTop(image, imgTop);
                        canvas.Children.Add(image);

                        Console.WriteLine($"FrameRenderer: Rendered texture '{tex.name}' at ({posX},{posY}) size ({image.Width}x{image.Height})");
                    }

                    Console.WriteLine($"FrameRenderer: Render complete - {textures.Count} textures, {regularFrames.Count} frames, {fontStrings.Count} labels");
                }, DispatcherPriority.Render);
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
