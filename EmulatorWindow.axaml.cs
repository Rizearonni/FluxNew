using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using System.IO;
using System.Linq;
using System;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace FluxNew
{
    public partial class EmulatorWindow : Window
    {
        private Canvas? _canvas;
        private ListBox? _addonList;
        private Canvas? _selectedHolder;
        private Border? _selectedBorder;

        // Inspector controls
        private TextBox? _posXBox;
        private TextBox? _posYBox;
        private TextBox? _widthBox;
        private TextBox? _heightBox;
        private TextBlock? _selectedFrameLabel;
        private TextBox? _emuConsole;
        private TextWriter? _previousOut;
        private TextWriter? _previousErr;
        private EmuTextWriter? _emuWriter;

        public EmulatorWindow()
        {
            InitializeComponent();

            _canvas = this.FindControl<Canvas>("EmuCanvas");

            _addonList = this.FindControl<ListBox>("AddonList");
            PopulateAddonList();
            if (_addonList != null)
            {
                _addonList.SelectionChanged += (s, e) =>
                {
                    try
                    {
                        var sel = _addonList.SelectedItem;
                        string? path = null;
                        if (sel is ListBoxItem lbi) path = lbi.Content?.ToString();
                        else path = sel?.ToString();
                        if (string.IsNullOrWhiteSpace(path)) return;

                        // compute full path
                        var cwd = Environment.CurrentDirectory;
                        var candidate = path.Replace('/', Path.DirectorySeparatorChar);
                        var full = Path.Combine(cwd, candidate);
                        if (Directory.Exists(full))
                        {
                            var (ok, framesStr) = WoWApi.TryLoadAddonDirectory(full);
                            if (ok)
                            {
                                if (!string.IsNullOrWhiteSpace(framesStr))
                                {
                                    // framesStr is JSON array produced by the Lua serializer
                                    var infos = new System.Collections.Generic.List<FrameInfo>();
                                    try
                                    {
                                        using var doc = JsonDocument.Parse(framesStr);
                                        var root = doc.RootElement;
                                        if (root.ValueKind == JsonValueKind.Array)
                                        {
                                            int idx = 0;
                                            foreach (var el in root.EnumerateArray())
                                            {
                                                var name = el.GetProperty("name").GetString() ?? ("frame" + idx);
                                                double x = el.TryGetProperty("x", out var px) && px.TryGetDouble(out var dx) ? dx : (80 + idx * 24);
                                                double y = el.TryGetProperty("y", out var py) && py.TryGetDouble(out var dy) ? dy : (80 + idx * 18);
                                                double w = el.TryGetProperty("w", out var pw) && pw.TryGetDouble(out var dw) ? dw : 220;
                                                double h = el.TryGetProperty("h", out var ph) && ph.TryGetDouble(out var dh) ? dh : 140;
                                                double scale = el.TryGetProperty("scale", out var ps) && ps.TryGetDouble(out var ds) ? ds : 1.0;

                                                var frame = CreateEmuFrame(x, y, Math.Max(24, w), Math.Max(24, h), string.IsNullOrEmpty(name) ? Path.GetFileName(full) : name);

                                                // apply scale transform if provided
                                                try
                                                {
                                                    if (Math.Abs(scale - 1.0) > 0.0001)
                                                    {
                                                        if (frame is Canvas holder)
                                                        {
                                                            var childBorder = holder.Children.OfType<Border>().FirstOrDefault();
                                                            if (childBorder != null)
                                                            {
                                                                childBorder.RenderTransform = new ScaleTransform(scale, scale);
                                                                childBorder.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
                                                            }
                                                        }
                                                    }
                                                }
                                                catch { }

                                                _canvas?.Children.Add(frame);

                                                var info = new FrameInfo
                                                {
                                                    Name = name ?? string.Empty,
                                                    Holder = frame as Canvas,
                                                    Border = (frame as Canvas)?.Children.OfType<Border>().FirstOrDefault(),
                                                    RawX = x,
                                                    RawY = y,
                                                    RawWidth = Math.Max(24, w),
                                                    RawHeight = Math.Max(24, h),
                                                    Scale = scale,
                                                    AnchorRaw = string.Empty,
                                                    ChildrenRaw = string.Empty
                                                };

                                                // populate anchors array from JSON
                                                if (el.TryGetProperty("anchors", out var anchorsEl) && anchorsEl.ValueKind == JsonValueKind.Array)
                                                {
                                                    foreach (var a in anchorsEl.EnumerateArray())
                                                    {
                                                        var anch = new Anchor();
                                                        anch.Point = a.TryGetProperty("point", out var ap) ? ap.GetString() ?? string.Empty : string.Empty;
                                                        anch.RelativeTo = a.TryGetProperty("relativeTo", out var ar) ? ar.GetString() ?? string.Empty : string.Empty;
                                                        anch.RelPoint = a.TryGetProperty("relPoint", out var rp) ? rp.GetString() ?? string.Empty : string.Empty;
                                                        anch.OffsetX = a.TryGetProperty("x", out var ax) && ax.TryGetDouble(out var adx) ? adx : 0.0;
                                                        anch.OffsetY = a.TryGetProperty("y", out var ay) && ay.TryGetDouble(out var ady) ? ady : 0.0;
                                                        info.Anchors.Add(anch);
                                                    }
                                                }

                                                // populate children list
                                                if (el.TryGetProperty("children", out var childrenEl) && childrenEl.ValueKind == JsonValueKind.Array)
                                                {
                                                    foreach (var c in childrenEl.EnumerateArray())
                                                    {
                                                        try { info.ChildNames.Add(c.GetString() ?? string.Empty); } catch { }
                                                    }
                                                }

                                                infos.Add(info);
                                                idx++;
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        AppendToEmuConsole("Failed to parse frames JSON: " + ex.Message);
                                    }

                                    // Resolve anchors and parent-child relationships
                                    ResolveAnchorsAndParenting(infos);
                                }
                                else
                                {
                                    // fallback single representative frame
                                    var frame = CreateEmuFrame(40, 40, 220, 140, Path.GetFileName(full));
                                    _canvas?.Children.Add(frame);
                                    Canvas.SetLeft(frame, 80);
                                    Canvas.SetTop(frame, 80);
                                }
                            }
                        }
                    }
                    catch { }
                };
            }

            // inspector controls
            _posXBox = this.FindControl<TextBox>("PosXBox");
            _posYBox = this.FindControl<TextBox>("PosYBox");
            _widthBox = this.FindControl<TextBox>("WidthBox");
            _heightBox = this.FindControl<TextBox>("HeightBox");
            _selectedFrameLabel = this.FindControl<TextBlock>("SelectedFrameLabel");
            _emuConsole = this.FindControl<TextBox>("EmuConsole");

            // Redirect global Console output to emulator console while this window is open
            try
            {
                _previousOut = Console.Out;
                _previousErr = Console.Error;
                _emuWriter = new EmuTextWriter(this);
                Console.SetOut(_emuWriter);
                Console.SetError(_emuWriter);
            }
            catch { }

            // Restore on close
            this.Closed += (_, __) =>
            {
                try
                {
                    if (_previousOut != null) Console.SetOut(_previousOut);
                    if (_previousErr != null) Console.SetError(_previousErr);
                }
                catch { }
            };

            var applyBtn = this.FindControl<Button>("ApplyButton");
            if (applyBtn != null) applyBtn.Click += (_, __) => ApplyInspectorChanges();
            var deleteBtn = this.FindControl<Button>("DeleteButton");
            if (deleteBtn != null) deleteBtn.Click += (_, __) => DeleteSelectedFrame();

            var addBtn = this.FindControl<Button>("AddFrameButton");
            if (addBtn != null) addBtn.Click += (_, __) => AddFrame();

            var closeBtn = this.FindControl<Button>("CloseButton");
            if (closeBtn != null) closeBtn.Click += (_, __) => this.Close();
        }

        private void AddFrame()
        {
            if (_canvas == null) return;

            var frame = CreateEmuFrame(50, 50, 240, 160, "Frame");
            _canvas.Children.Add(frame);
            Canvas.SetLeft(frame, 60);
            Canvas.SetTop(frame, 60);
            AppendToEmuConsole($"Added frame 'Frame' at 60,60 (240x160)");
        }

        private Control CreateEmuFrame(double left, double top, double width, double height, string titleText)
        {
            var border = new Border
            {
                Width = width,
                Height = height,
                Background = (IBrush?)Application.Current?.FindResource("BackgroundBrush") ?? Brushes.DarkGray,
                BorderBrush = (IBrush?)Application.Current?.FindResource("AccentBrush") ?? Brushes.CornflowerBlue,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4)
            };

            // content
            var title = new TextBlock { Text = titleText ?? "Frame", Margin = new Thickness(6), Foreground = (IBrush?)Application.Current?.FindResource("ForegroundBrush") ?? Brushes.White };
            var grid = new Grid();
            grid.Children.Add(title);
            border.Child = grid;

            // dragging state
            bool dragging = false;
            Point dragStart = default;
            double startLeft = 0, startTop = 0;

            // resizing
            bool resizing = false;
            Point resizeStart = default;
            double startW = 0, startH = 0;

            // resize grip
            var grip = new Border
            {
                Width = 14,
                Height = 14,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                CornerRadius = new CornerRadius(2)
            };

            Canvas.SetRight(grip, 4);
            Canvas.SetBottom(grip, 4);

            var holder = new Canvas();
            holder.Children.Add(border);
            holder.Children.Add(grip);

            // pointer events for dragging (and selection)
            border.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(border).Properties.IsRightButtonPressed) return;
                // selection first
                SelectFrame(holder, border);
                dragging = true;
                dragStart = e.GetPosition(_canvas);
                startLeft = Canvas.GetLeft(holder);
                startTop = Canvas.GetTop(holder);
                // pointer capture removed for compatibility; drag is handled without explicit capture
                e.Handled = true;
            };

            border.PointerMoved += (s, e) =>
            {
                if (!dragging) return;
                var cur = e.GetPosition(_canvas);
                var dx = cur.X - dragStart.X;
                var dy = cur.Y - dragStart.Y;
                Canvas.SetLeft(holder, Math.Max(0, startLeft + dx));
                Canvas.SetTop(holder, Math.Max(0, startTop + dy));
                e.Handled = true;
            };

            border.PointerReleased += (s, e) =>
            {
                if (dragging)
                {
                    dragging = false;
                    // pointer release removed
                    try
                    {
                        var l = (int)Canvas.GetLeft(holder);
                        var t = (int)Canvas.GetTop(holder);
                        AppendToEmuConsole($"Moved frame '{titleText}' to {l},{t}");
                    }
                    catch { }
                }
            };

            // resize grip handlers
            grip.PointerPressed += (s, e) =>
            {
                // select before resizing
                SelectFrame(holder, border);
                resizing = true;
                resizeStart = e.GetPosition(_canvas);
                startW = border.Width;
                startH = border.Height;
                // pointer capture removed for compatibility; resize handled without explicit capture
                e.Handled = true;
            };
            grip.PointerMoved += (s, e) =>
            {
                if (!resizing) return;
                var cur = e.GetPosition(_canvas);
                var dx = cur.X - resizeStart.X;
                var dy = cur.Y - resizeStart.Y;
                border.Width = Math.Max(24, startW + dx);
                border.Height = Math.Max(24, startH + dy);
                e.Handled = true;
            };
            grip.PointerReleased += (s, e) =>
            {
                if (resizing)
                {
                    resizing = false;
                    // pointer release removed
                    try
                    {
                        AppendToEmuConsole($"Resized frame '{titleText}' to {border.Width}x{border.Height}");
                    }
                    catch { }
                }
            };

            // ensure holder sizing matches border
            border.AttachedToVisualTree += (_, __) =>
            {
                Canvas.SetLeft(holder, left);
                Canvas.SetTop(holder, top);
            };

            // helper to set initial style
            void ClearSelectionVisual()
            {
                try
                {
                    border.BorderBrush = (IBrush?)Application.Current?.FindResource("AccentBrush") ?? Brushes.CornflowerBlue;
                }
                catch { }
            }

            ClearSelectionVisual();

            return holder;
        }

        private void SelectFrame(Canvas holder, Border border)
        {
            try
            {
                // clear previous selection visual
                if (_selectedBorder != null && !_selectedBorder.Equals(border))
                {
                    _selectedBorder.BorderBrush = (IBrush?)Application.Current?.FindResource("AccentBrush") ?? Brushes.CornflowerBlue;
                }

                _selectedHolder = holder;
                _selectedBorder = border;

                // highlight
                _selectedBorder.BorderBrush = Brushes.Gold;

                // populate inspector
                var left = Canvas.GetLeft(holder);
                var top = Canvas.GetTop(holder);
                _posXBox!.Text = ((int)left).ToString();
                _posYBox!.Text = ((int)top).ToString();
                _widthBox!.Text = ((int)border.Width).ToString();
                _heightBox!.Text = ((int)border.Height).ToString();
                _selectedFrameLabel!.Text = "Frame";
            }
            catch
            {
                // ignore
            }
        }

        private void ApplyInspectorChanges()
        {
            if (_selectedHolder == null || _selectedBorder == null) return;
            try
            {
                if (int.TryParse(_posXBox?.Text, out var x)) Canvas.SetLeft(_selectedHolder, x);
                if (int.TryParse(_posYBox?.Text, out var y)) Canvas.SetTop(_selectedHolder, y);
                if (int.TryParse(_widthBox?.Text, out var w)) _selectedBorder.Width = Math.Max(8, w);
                if (int.TryParse(_heightBox?.Text, out var h)) _selectedBorder.Height = Math.Max(8, h);
            }
            catch { }
        }

        private void DeleteSelectedFrame()
        {
            if (_selectedHolder == null) return;
            try
            {
                _canvas?.Children.Remove(_selectedHolder);
                _selectedHolder = null;
                _selectedBorder = null;
                _posXBox!.Text = string.Empty;
                _posYBox!.Text = string.Empty;
                _widthBox!.Text = string.Empty;
                _heightBox!.Text = string.Empty;
                _selectedFrameLabel!.Text = "(none)";
            }
            catch { }
        }

        private void AppendToEmuConsole(string message)
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (_emuConsole != null)
                        {
                            _emuConsole.Text += message + Environment.NewLine;
                            _emuConsole.CaretIndex = _emuConsole.Text?.Length ?? 0;
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        // Internal helpers for anchor resolution and parenting
        private class FrameInfo
        {
            public string Name = string.Empty;
            public Canvas? Holder;
            public Border? Border;
            public double RawX;
            public double RawY;
            public double RawWidth;
            public double RawHeight;
            public double Scale = 1.0;
            public string AnchorRaw = string.Empty;
            public string ChildrenRaw = string.Empty;
            public System.Collections.Generic.List<Anchor> Anchors = new System.Collections.Generic.List<Anchor>();
            public System.Collections.Generic.List<string> ChildNames = new System.Collections.Generic.List<string>();
            public string? ParentName;
        }

        private class Anchor
        {
            public string Point = string.Empty;
            public string RelativeTo = string.Empty;
            public string RelPoint = string.Empty;
            public double OffsetX = 0;
            public double OffsetY = 0;
        }

        private void ResolveAnchorsAndParenting(System.Collections.Generic.List<FrameInfo> infos)
        {
            if (_canvas == null || infos == null) return;

            var dict = infos.Where(i => !string.IsNullOrEmpty(i.Name)).ToDictionary(i => i.Name, i => i);

            // parse anchors and children
            foreach (var f in infos)
            {
                // If AnchorRaw is present, parse it; otherwise assume Anchors was populated programmatically from JSON
                if (!string.IsNullOrWhiteSpace(f.AnchorRaw))
                {
                    f.Anchors.Clear();
                    var ancs = f.AnchorRaw.Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var a in ancs)
                    {
                        var parts = a.Split(new[] { ',' });
                        var anch = new Anchor();
                        if (parts.Length > 0) anch.Point = parts[0].Trim();
                        if (parts.Length > 1) anch.RelativeTo = parts[1].Trim();
                        if (parts.Length > 2) anch.RelPoint = parts[2].Trim();
                        if (parts.Length > 3) double.TryParse(parts[3], out anch.OffsetX);
                        if (parts.Length > 4) double.TryParse(parts[4], out anch.OffsetY);
                        f.Anchors.Add(anch);
                    }
                }

                f.ChildNames.Clear();
                if (!string.IsNullOrWhiteSpace(f.ChildrenRaw))
                {
                    var ch = f.ChildrenRaw.Split(new[] { '~' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var c in ch) f.ChildNames.Add(c.Trim());
                }
            }

            // set initial absolute positions from raw values
            var absX = new System.Collections.Generic.Dictionary<FrameInfo, double>();
            var absY = new System.Collections.Generic.Dictionary<FrameInfo, double>();
            foreach (var f in infos)
            {
                absX[f] = f.RawX;
                absY[f] = f.RawY;
            }

            // helper to get anchor offset within a frame (in pixels)
            (double x, double y) GetAnchorOffset(FrameInfo fi, string point)
            {
                double w = fi.RawWidth * fi.Scale;
                double h = fi.RawHeight * fi.Scale;
                var p = (point ?? string.Empty).ToUpperInvariant();
                switch (p)
                {
                    case "TOPLEFT": return (0, 0);
                    case "TOP": return (w / 2.0, 0);
                    case "TOPRIGHT": return (w, 0);
                    case "LEFT": return (0, h / 2.0);
                    case "CENTER":
                    case "MIDDLE": return (w / 2.0, h / 2.0);
                    case "RIGHT": return (w, h / 2.0);
                    case "BOTTOMLEFT": return (0, h);
                    case "BOTTOM": return (w / 2.0, h);
                    case "BOTTOMRIGHT": return (w, h);
                    case "": return (0, 0);
                    default: return (0, 0);
                }
            }

            // iterative resolution: try to resolve anchors using available target positions
            int maxPasses = Math.Max(3, infos.Count + 2);
            for (int pass = 0; pass < maxPasses; pass++)
            {
                foreach (var f in infos)
                {
                    if (f.Anchors.Count == 0) continue;
                    var a = f.Anchors[0]; // use first anchor for placement

                    // determine target absolute anchor
                    double targetAnchorX = 0, targetAnchorY = 0;

                    if (string.IsNullOrWhiteSpace(a.RelativeTo) || a.RelativeTo.Equals("UIParent", StringComparison.OrdinalIgnoreCase))
                    {
                        // use canvas origin
                        targetAnchorX = 0;
                        targetAnchorY = 0;
                    }
                    else if (dict.TryGetValue(a.RelativeTo, out var targetInfo))
                    {
                        // ensure target has an absolute position recorded
                        var tX = absX.ContainsKey(targetInfo) ? absX[targetInfo] : targetInfo.RawX;
                        var tY = absY.ContainsKey(targetInfo) ? absY[targetInfo] : targetInfo.RawY;
                        var relOffset = GetAnchorOffset(targetInfo, a.RelPoint);
                        targetAnchorX = tX + relOffset.x;
                        targetAnchorY = tY + relOffset.y;
                    }
                    else
                    {
                        // relativeTo not found; fallback to raw X/Y
                        targetAnchorX = 0;
                        targetAnchorY = 0;
                    }

                    var ownOffset = GetAnchorOffset(f, a.Point);
                    var newLeft = targetAnchorX + a.OffsetX - ownOffset.x;
                    var newTop = targetAnchorY + a.OffsetY - ownOffset.y;

                    absX[f] = newLeft;
                    absY[f] = newTop;
                }
            }

            // apply computed absolute positions
            foreach (var f in infos)
            {
                try
                {
                    if (f.Holder != null)
                    {
                        Canvas.SetLeft(f.Holder, absX[f]);
                        Canvas.SetTop(f.Holder, absY[f]);
                    }
                }
                catch { }
            }

            // compute parent relationship from children lists
            foreach (var f in infos)
            {
                foreach (var childName in f.ChildNames)
                {
                    if (dict.TryGetValue(childName, out var childInfo))
                    {
                        childInfo.ParentName = f.Name;
                    }
                }
            }

            // perform parenting: remove child from root canvas and add to parent holder, converting coords to local
            foreach (var child in infos.Where(i => !string.IsNullOrWhiteSpace(i.ParentName)))
            {
                if (!dict.TryGetValue(child.ParentName!, out var parent)) continue;
                if (child.Holder == null || parent.Holder == null) continue;

                try
                {
                    // absolute positions
                    var childAbsX = absX.ContainsKey(child) ? absX[child] : child.RawX;
                    var childAbsY = absY.ContainsKey(child) ? absY[child] : child.RawY;
                    var parentAbsX = absX.ContainsKey(parent) ? absX[parent] : parent.RawX;
                    var parentAbsY = absY.ContainsKey(parent) ? absY[parent] : parent.RawY;

                    // remove from root canvas
                    try { _canvas.Children.Remove(child.Holder); } catch { }

                    // add to parent's canvas holder
                    parent.Holder.Children.Add(child.Holder);

                    // set local coords relative to parent
                    Canvas.SetLeft(child.Holder, childAbsX - parentAbsX);
                    Canvas.SetTop(child.Holder, childAbsY - parentAbsY);
                }
                catch { }
            }
        }

        private class EmuTextWriter : TextWriter
        {
            private readonly EmulatorWindow _win;
            public EmuTextWriter(EmulatorWindow win) { _win = win; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void WriteLine(string? value)
            {
                _win.AppendToEmuConsole(value ?? string.Empty);
            }
            public override void Write(char value)
            {
                _win.AppendToEmuConsole(value.ToString());
            }
            public override void Write(string? value)
            {
                _win.AppendToEmuConsole(value ?? string.Empty);
            }
        }

        private void PopulateAddonList()
        {
            try
            {
                if (_addonList == null) return;
                if (_addonList.Items is System.Collections.IList existing) existing.Clear();
                var cwd = Environment.CurrentDirectory;
                var addonsDir = Path.Combine(cwd, "addons");
                if (!Directory.Exists(addonsDir)) return;

                var dirs = Directory.GetDirectories(addonsDir);
                foreach (var d in dirs)
                {
                    var rel = Path.Combine("addons", Path.GetFileName(d)).Replace(Path.DirectorySeparatorChar, '/');
                    var item = new ListBoxItem { Content = rel };
                    if (_addonList.Items is System.Collections.IList il) il.Add(item);
                }
            }
            catch { }
        }
    }
}
