using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using System.IO;
using System.Linq;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace FluxNew
{
    public partial class EmulatorWindow : Window
    {
        // cached textures root (optional). If a folder named "textures" exists under the app base dir,
        // we'll search it for game-like assets (provided by user/repo).
        private string? _texturesRoot;
        // Path of the last addon folder that was loaded into the emulator; used to resolve textures
        private string? _lastLoadedAddonPath;

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

        // Console buffer to avoid per-character UI updates
        private readonly System.Text.StringBuilder _consoleBuffer = new System.Text.StringBuilder();
        private readonly object _consoleLock = new object();
        private bool _consoleFlushScheduled = false;

        // Module toggles
        private Avalonia.Controls.CheckBox? _captureConsoleCheck;
        private Avalonia.Controls.CheckBox? _chatCheck;
        private Avalonia.Controls.CheckBox? _unitFramesCheck;
        private Avalonia.Controls.CheckBox? _mapCheck;
        private Avalonia.Controls.CheckBox? _bagsCheck;

        // simple registry for module add/remove handlers
        private readonly System.Collections.Generic.Dictionary<string, System.Action<bool>> _moduleHandlers = new System.Collections.Generic.Dictionary<string, System.Action<bool>>();
        
        // Unit frame simulation and controls
        private Avalonia.Threading.DispatcherTimer? _unitFrameTimer;
        private readonly Random _rand = new Random();
        private readonly System.Collections.Generic.Dictionary<string, (Avalonia.Controls.ProgressBar hp, Avalonia.Controls.ProgressBar pow, TextBlock hpText, Border portrait, RenderHelpers.CastBarControl castBar, TextBlock castText)> _unitBars
            = new System.Collections.Generic.Dictionary<string, (Avalonia.Controls.ProgressBar, Avalonia.Controls.ProgressBar, TextBlock, Border, RenderHelpers.CastBarControl, TextBlock)>(StringComparer.OrdinalIgnoreCase);

        // simple per-unit cast progress tracking (0-100)
        private readonly System.Collections.Generic.Dictionary<string, double> _castProgress = new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // live frames/objects created by addons at runtime via Flux.HostCall
        private readonly System.Collections.Generic.Dictionary<string, Canvas> _liveFrames = new System.Collections.Generic.Dictionary<string, Canvas>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string, Control> _liveObjects = new System.Collections.Generic.Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string, string> _registeredHandlers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // LDB auto-created minimap buttons registry
        private readonly System.Collections.Generic.Dictionary<string, Control> _ldbButtons = new System.Collections.Generic.Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);

        private string? FindTextureForName(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                if (_texturesRoot == null)
                {
                    var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                    var candidates = new[] {
                        Path.Combine(baseDir, "textures"),
                        Path.Combine(baseDir, "wow-ui-textures"),
                        Path.Combine(baseDir, "textures", "wow-ui-textures"),
                        Path.Combine(baseDir, "assets", "textures"),
                        Path.Combine(baseDir, "assets", "wow-ui-textures"),
                        Path.Combine(baseDir, "assets")
                    };
                    _texturesRoot = candidates.FirstOrDefault(d => Directory.Exists(d));
                }

                if (string.IsNullOrEmpty(_texturesRoot)) return null;

                var lname = name.ToLowerInvariant();
                var exts = new[] { ".png", ".jpg", ".jpeg", ".gif" };
                foreach (var f in Directory.EnumerateFiles(_texturesRoot, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fn = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                        if (fn.Contains(lname) || lname.Contains(fn))
                        {
                            var ext = Path.GetExtension(f).ToLowerInvariant();
                            if (exts.Contains(ext)) return f;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // Reposition all auto-created LDB buttons radially around the minimap host
        private void RepositionLdbButtons(Canvas host)
        {
            try
            {
                if (host == null) return;
                var btns = _ldbButtons.Values.ToList();
                if (btns.Count == 0) return;
                var hb = host.Bounds;
                // if not measured yet, skip
                if (hb.Width <= 0 || hb.Height <= 0) return;
                var cx = hb.Width / 2.0;
                var cy = hb.Height / 2.0;
                var btnSize = btns[0].Bounds.Width > 0 ? btns[0].Bounds.Width : 28.0;
                var radius = Math.Max(24, Math.Min(cx, cy) - btnSize - 4);
                var step = 360.0 / btns.Count;
                // start angle (degrees) - place at top-right-ish by default
                var start = -45.0;
                for (int i = 0; i < btns.Count; i++)
                {
                    var angle = (start + i * step) * Math.PI / 180.0;
                    var dx = Math.Cos(angle) * radius;
                    var dy = -Math.Sin(angle) * radius; // invert y
                    var left = cx + dx - btnSize / 2.0;
                    var top = cy + dy - btnSize / 2.0;
                    var ctl = btns[i];
                    Canvas.SetLeft(ctl, left);
                    Canvas.SetTop(ctl, top);
                }
            }
            catch { }
        }

        public EmulatorWindow()
        {
            InitializeComponent();

            // inspector controls
            _posXBox = this.FindControl<TextBox>("PosXBox");
            _posYBox = this.FindControl<TextBox>("PosYBox");
            _widthBox = this.FindControl<TextBox>("WidthBox");
            _heightBox = this.FindControl<TextBox>("HeightBox");
            _selectedFrameLabel = this.FindControl<TextBlock>("SelectedFrameLabel");
            _emuConsole = this.FindControl<TextBox>("EmuConsole");

            // Find capture checkbox and module toggles (console capture disabled by default)
            try
            {
                _captureConsoleCheck = this.FindControl<Avalonia.Controls.CheckBox>("CaptureConsoleCheck");
                _chatCheck = this.FindControl<Avalonia.Controls.CheckBox>("ChatModuleCheck");
                _unitFramesCheck = this.FindControl<Avalonia.Controls.CheckBox>("UnitFramesModuleCheck");
                _mapCheck = this.FindControl<Avalonia.Controls.CheckBox>("MapModuleCheck");
                _bagsCheck = this.FindControl<Avalonia.Controls.CheckBox>("BagsModuleCheck");

                if (_captureConsoleCheck != null)
                {
                    _captureConsoleCheck.IsCheckedChanged += (_, __) =>
                    {
                        try { if (_captureConsoleCheck.IsChecked == true) EnableConsoleCapture(true); else EnableConsoleCapture(false); } catch { }
                    };
                }

                // Enable console capture by default for easier debugging inside the emulator.
                try { EnableConsoleCapture(true); if (_captureConsoleCheck != null) _captureConsoleCheck.IsChecked = true; } catch { }

                // Wire addon list and addon folder button
                try
                {
                    _canvas = this.FindControl<Canvas>("EmuCanvas");
                    _addonList = this.FindControl<ListBox>("AddonList");
                    var loadBtn = this.FindControl<Button>("LoadAddonFolderButton");
                    var refreshBtn = this.FindControl<Button>("RefreshAddonsButton");

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

                                var cwd = Environment.CurrentDirectory;
                                var candidate = path.Replace('/', Path.DirectorySeparatorChar);
                                var full = Path.Combine(cwd, candidate);
                                if (Directory.Exists(full))
                                {
                                    var (ok, framesStr) = WoWApi.TryLoadAddonDirectory(full);
                                    if (ok && !string.IsNullOrWhiteSpace(framesStr))
                                    {
                                        try
                                        {
                                            _lastLoadedAddonPath = full;
                                            LoadFramesFromJson(framesStr, Path.GetFileName(full), full);
                                            // Dump runtime diagnostics after addon load to help debug slash/minimap registrations
                                            try { var diag = WoWApi.DumpRuntimeDiagnostics(); if (!string.IsNullOrWhiteSpace(diag)) AppendToEmuConsole("Runtime diagnostics:\n" + diag); } catch { }
                                        }
                                        catch (Exception ex) { AppendToEmuConsole("Failed to load frames into emulator: " + ex.Message); }
                                    }
                                    else
                                    {
                                        // fallback: create representative frame
                                        var frame = CreateEmuFrame(40, 40, 220, 140, Path.GetFileName(full));
                                        _canvas?.Children.Add(frame);
                                        Canvas.SetLeft(frame, 80);
                                        Canvas.SetTop(frame, 80);
                                    }
                                }
                            }
                            catch { }
                        };
                    }

                    if (loadBtn != null)
                    {
                        loadBtn.Click += async (_, __) =>
                        {
                                try
                                {
#pragma warning disable CS0618 // OpenFolderDialog is obsolete; keep for compatibility
                                    var dlg = new OpenFolderDialog();
                                    dlg.Title = "Select addon folder to load";
                                    var folder = await dlg.ShowAsync(this);
#pragma warning restore CS0618
                                if (string.IsNullOrWhiteSpace(folder)) return;
                                AppendToEmuConsole($"Loading addon from: {folder}");
                                var (ok, frames) = WoWApi.TryLoadAddonDirectory(folder);
                                AppendToEmuConsole($"TryLoadAddonDirectory: success={ok}, framesLen={(frames?.Length ?? 0)}");
                                if (ok && !string.IsNullOrWhiteSpace(frames))
                                {
                                    try
                                    {
                                        _lastLoadedAddonPath = folder;
                                            LoadFramesFromJson(frames, Path.GetFileName(folder), folder);
                                        AppendToEmuConsole("Emulator loaded frames from folder.");
                                        // Dump runtime diagnostics after addon load to help debug slash/minimap registrations
                                        try { var diag = WoWApi.DumpRuntimeDiagnostics(); if (!string.IsNullOrWhiteSpace(diag)) AppendToEmuConsole("Runtime diagnostics:\n" + diag); } catch { }
                                        // Scan LibDataBroker objects and auto-create minimap buttons for brokers that look actionable
                                        try
                                        {
                                            var ldb = WoWApi.ScanLibDataBroker();
                                            if (!string.IsNullOrWhiteSpace(ldb))
                                            {
                                                AppendToEmuConsole("LibDataBroker objects:\n" + ldb);
                                                var lines = ldb.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                                foreach (var ln in lines)
                                                {
                                                    try
                                                    {
                                                        var parts = ln.Split(new[] { '|' }, 5);
                                                        if (parts.Length < 5) continue;
                                                        // parts: LDB|name|type|hasOnClick|icon
                                                        var name = parts[1];
                                                        var type = parts[2];
                                                        var hasOnClick = parts[3] == "1";
                                                        var icon = parts[4];
                                                        // If broker has OnClick or is type 'launcher', create a minimap button
                                                        if (hasOnClick || (!string.IsNullOrWhiteSpace(type) && type == "launcher") )
                                                        {
                                                            // only create if not already created via LibDBIcon
                                                            try
                                                            {
                                                                var mapHolder = _canvas?.Children.OfType<Canvas>().FirstOrDefault(c => string.Equals(c.Name, "mod_map_holder", StringComparison.OrdinalIgnoreCase));
                                                                if (mapHolder == null) continue;
                                                                var innerBorder = mapHolder.Children.OfType<Border>().FirstOrDefault();
                                                                Grid? grid = innerBorder?.Child as Grid;
                                                                var content = grid?.Children.OfType<Grid>().FirstOrDefault(x => x.Name == "ContentArea");
                                                                if (content == null) continue;
                                                                var host = content.Children.OfType<Canvas>().FirstOrDefault(c => c.Name == "MinimapButtonsHost");
                                                                if (host == null)
                                                                {
                                                                    host = new Canvas { Name = "MinimapButtonsHost", IsHitTestVisible = true };
                                                                    content.Children.Add(host);
                                                                }

                                                                double btnSize = 28;
                                                                Control createdCtl = null;
                                                                Bitmap? bmp = null;
                                                                if (!string.IsNullOrWhiteSpace(icon) && icon != "(func)")
                                                                {
                                                                    try { var td = TextureCache.LoadTexture(icon); if (td != null) bmp = BitmapFromTextureData(td); } catch { }
                                                                }
                                                                if (bmp != null)
                                                                {
                                                                    // create a rounded border background and put the image inside for nicer visuals
                                                                    var border = new Border
                                                                    {
                                                                        Width = btnSize,
                                                                        Height = btnSize,
                                                                        CornerRadius = new CornerRadius(btnSize / 2.0),
                                                                        Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                                                                        BorderBrush = Brushes.Gray,
                                                                        BorderThickness = new Thickness(1),
                                                                        Padding = new Thickness(3),
                                                                        Child = new Image { Source = bmp, Stretch = Avalonia.Media.Stretch.Uniform }
                                                                    };
                                                                    createdCtl = border;
                                                                    // Tooltip with broker name
                                                                    try { Avalonia.Controls.ToolTip.SetTip(border, name); } catch { }
                                                                    // hover/visual changes handled via tooltip; keep visuals simple for compatibility
                                                                    // click invokes broker OnClick via pointer press on the wrapper
                                                                    border.PointerPressed += (_, __) =>
                                                                    {
                                                                        Task.Run(() =>
                                                                        {
                                                                            try
                                                                            {
                                                                                var safe = name.Replace("'", "\\'").Replace("\\", "\\\\");
                                                                                KopiLuaRunner.TryRun($"if LibStub and LibStub._libs and LibStub._libs['LibDataBroker-1.1'] and LibStub._libs['LibDataBroker-1.1'].objects['{safe}'] and type(LibStub._libs['LibDataBroker-1.1'].objects['{safe}'].OnClick)=='function' then LibStub._libs['LibDataBroker-1.1'].objects['{safe}'].OnClick('LeftButton') end");
                                                                                Dispatcher.UIThread.Post(() => AppendToEmuConsole($"LDB broker '{name}' clicked and attempted OnClick invocation."), Avalonia.Threading.DispatcherPriority.Background);
                                                                            }
                                                                            catch (Exception ex) { Dispatcher.UIThread.Post(() => AppendToEmuConsole("LDB click invoke failed: " + ex.Message)); }
                                                                        });
                                                                    };
                                                                }
                                                                else
                                                                {
                                                                    // fallback rounded text button
                                                                    var border = new Border
                                                                    {
                                                                        Width = btnSize,
                                                                        Height = btnSize,
                                                                        CornerRadius = new CornerRadius(btnSize / 2.0),
                                                                        Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                                                                        BorderBrush = Brushes.Gray,
                                                                        BorderThickness = new Thickness(1),
                                                                        Child = new TextBlock { Text = name, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 10, TextWrapping = TextWrapping.Wrap }
                                                                    };
                                                                    createdCtl = border;
                                                                    try { Avalonia.Controls.ToolTip.SetTip(border, name); } catch { }
                                                                    // hover effects not available in this Avalonia version; tooltip present
                                                                    border.PointerPressed += (_, __) =>
                                                                    {
                                                                        Task.Run(() =>
                                                                        {
                                                                            try
                                                                            {
                                                                                var safe = name.Replace("'", "\\'").Replace("\\", "\\\\");
                                                                                KopiLuaRunner.TryRun($"if LibStub and LibStub._libs and LibStub._libs['LibDataBroker-1.1'] and LibStub._libs['LibDataBroker-1.1'].objects['{safe}'] and type(LibStub._libs['LibDataBroker-1.1'].objects['{safe}'].OnClick)=='function' then LibStub._libs['LibDataBroker-1.1'].objects['{safe}'].OnClick('LeftButton') end");
                                                                                Dispatcher.UIThread.Post(() => AppendToEmuConsole($"LDB broker '{name}' clicked and attempted OnClick invocation."), Avalonia.Threading.DispatcherPriority.Background);
                                                                            }
                                                                            catch (Exception ex) { Dispatcher.UIThread.Post(() => AppendToEmuConsole("LDB click invoke failed: " + ex.Message)); }
                                                                        });
                                                                    };
                                                                }

                                                                // avoid duplicates
                                                                if (_ldbButtons.ContainsKey(name))
                                                                {
                                                                    var existing = _ldbButtons[name];
                                                                    existing.IsVisible = true;
                                                                }
                                                                else
                                                                {
                                                                    host.Children.Add(createdCtl);
                                                                    _ldbButtons[name] = createdCtl;
                                                                    // reposition buttons radially when layout is available
                                                                    void RepositionHandler(object? s, VisualTreeAttachmentEventArgs a)
                                                                    {
                                                                        try { RepositionLdbButtons(host); }
                                                                        catch { }
                                                                    }
                                                                    createdCtl.AttachedToVisualTree += RepositionHandler;
                                                                }
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                        catch { }

                                        // Attempt to auto-invoke common addon UI openers (e.g., Attune_Show, Attune_Frame.Show)
                                        try
                                        {
                                            var resOpen = WoWApi.InvokeAddonUiOpeners(Path.GetFileName(folder));
                                            if (!string.IsNullOrWhiteSpace(resOpen)) AppendToEmuConsole("Auto-open attempt:\n" + resOpen);
                                        }
                                        catch { }
                                    }
                                    catch (Exception ex) { AppendToEmuConsole($"Error loading frames into emulator: {ex.Message}"); }
                                }
                            }
                            catch (Exception ex)
                            {
                                AppendToEmuConsole($"Load addon folder failed: {ex.Message}");
                            }
                        };
                    }

                    if (refreshBtn != null)
                    {
                        refreshBtn.Click += (_, __) => { PopulateAddonList(); AppendToEmuConsole("Addon list refreshed."); };
                    }
                }
                catch { }

                // module toggles
                RegisterModuleHandlers();

                if (_chatCheck != null)
                {
                    _chatCheck.IsCheckedChanged += (_, __) => { try { ToggleModule("chat", _chatCheck.IsChecked == true); } catch { } };
                }
                if (_unitFramesCheck != null)
                {
                    _unitFramesCheck.IsCheckedChanged += (_, __) => { try { ToggleModule("unitframes", _unitFramesCheck.IsChecked == true); } catch { } };
                }
                if (_mapCheck != null)
                {
                    _mapCheck.IsCheckedChanged += (_, __) => { try { ToggleModule("map", _mapCheck.IsChecked == true); } catch { } };
                }
                if (_bagsCheck != null)
                {
                    _bagsCheck.IsCheckedChanged += (_, __) => { try { ToggleModule("bags", _bagsCheck.IsChecked == true); } catch { } };
                }

                    // Expansion selector radios
                    try
                    {
                        var expVan = this.FindControl<RadioButton>("ExpVanillaRadio");
                        var expMists = this.FindControl<RadioButton>("ExpMistsRadio");
                        var expMain = this.FindControl<RadioButton>("ExpMainlineRadio");
                        if (expVan != null) expVan.IsCheckedChanged += (_, __) => { try { if (expVan.IsChecked == true) { WoWApi.SetExpansion("Vanilla"); AppendToEmuConsole("Expansion set to Vanilla"); } } catch { } };
                        if (expMists != null) expMists.IsCheckedChanged += (_, __) => { try { if (expMists.IsChecked == true) { WoWApi.SetExpansion("Mists"); AppendToEmuConsole("Expansion set to Mists"); } } catch { } };
                        if (expMain != null) expMain.IsCheckedChanged += (_, __) => { try { if (expMain.IsChecked == true) { WoWApi.SetExpansion("Mainline"); AppendToEmuConsole("Expansion set to Mainline"); } } catch { } };
                        // default to Mainline
                        try { if (expMain != null) { expMain.IsChecked = true; WoWApi.SetExpansion("Mainline"); } }
                        catch { }
                    }
                    catch { }

                    // Edit presets button
                    try
                    {
                        var editBtn = this.FindControl<Button>("EditPresetsButton");
                        if (editBtn != null)
                        {
                            editBtn.Click += async (_, __) =>
                            {
                                try
                                {
                                    var win = new PresetEditorWindow();
                                    await win.ShowDialog(this);
                                    AppendToEmuConsole("Presets updated.");
                                }
                                catch (Exception ex) { AppendToEmuConsole("Failed to open presets editor: " + ex.Message); }
                            };
                        }
                    }
                    catch { }
            }
            catch { }

            // Restore on close: ensure we stop capturing console if active
            this.Closed += (_, __) =>
            {
                try
                {
                    EnableConsoleCapture(false);
                }
                catch { }
            };

            // Start polling Lua host calls (Flux.HostCall) so addons can update emulator UI
            try
            {
                var pollTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(300), Avalonia.Threading.DispatcherPriority.Background, (s, e) =>
                {
                    try
                    {
                        var raw = WoWApi.PollHostCalls();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            var lines = raw.Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
                            foreach (var ln in lines)
                            {
                                var parts = ln.Split(new[] { '|' });
                                if (parts.Length == 0) continue;
                                var cmd = parts[0];
                                if (string.Equals(cmd, "SetUnitHealth", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                                {
                                    var name = parts[1];
                                    if (double.TryParse(parts[2], out var v)) EmulatorHost.SetUnitHealth(name, v);
                                }
                                else if (string.Equals(cmd, "SetUnitPower", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                                {
                                    var name = parts[1];
                                    if (double.TryParse(parts[2], out var v)) EmulatorHost.SetUnitPower(name, v);
                                }
                                else if (string.Equals(cmd, "SetUnit", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                                {
                                    var name = parts[1];
                                    if (double.TryParse(parts[2], out var hp)) EmulatorHost.SetUnitHealth(name, hp);
                                    if (double.TryParse(parts[3], out var pw)) EmulatorHost.SetUnitPower(name, pw);
                                }
                                else if (string.Equals(cmd, "CreateFrame", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                                {
                                    // CreateFrame|name|ftype|parent
                                    var name = parts[1];
                                    var ftype = parts[2];
                                    var parent = parts[3];
                                    try
                                    {
                                        if (!_liveFrames.ContainsKey(name))
                                        {
                                            var holder = CreateEmuFrame(80, 80, 220, 140, string.IsNullOrEmpty(name) ? ftype : name);
                                            _canvas?.Children.Add(holder);
                                            // clamp initial placement into canvas
                                            try
                                            {
                                                double w = 0, h = 0;
                                                if (holder is Canvas tmpHolderCanvas)
                                                {
                                                    var b = tmpHolderCanvas.Children.OfType<Border>().FirstOrDefault();
                                                    if (b != null) { w = b.Width; h = b.Height; }
                                                }
                                                var (cl, ct) = ClampToCanvas(80, 80, w, h);
                                                Canvas.SetLeft(holder, cl);
                                                Canvas.SetTop(holder, ct);
                                            }
                                            catch { Canvas.SetLeft(holder, 80); Canvas.SetTop(holder, 80); }
                                            if (holder is Canvas holderCanvas) _liveFrames[name] = holderCanvas;
                                            AppendToEmuConsole($"Created frame '{name}' (type={ftype})");
                                        }
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "FrameSetSize", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                                {
                                    var name = parts[1];
                                    if (_liveFrames.TryGetValue(name, out var holder))
                                    {
                                        try
                                        {
                                            var border = holder.Children.OfType<Border>().FirstOrDefault();
                                            if (border != null)
                                            {
                                                if (double.TryParse(parts[2], out var w)) border.Width = Math.Max(8, w);
                                                if (double.TryParse(parts[3], out var h)) border.Height = Math.Max(8, h);
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                else if (string.Equals(cmd, "FrameSetPoint", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 7)
                                {
                                    var name = parts[1];
                                    var point = parts[2];
                                    var relativeTo = parts[3];
                                    var relPoint = parts[4];
                                    var sx = parts[5];
                                    var sy = parts[6];
                                    try
                                    {
                                        if (_liveFrames.TryGetValue(name, out var holder))
                                        {
                                            double x = 0, y = 0;
                                            double.TryParse(sx, out x);
                                            double.TryParse(sy, out y);
                                            // If relativeTo is specified and known, position relative to that frame
                                            if (!string.IsNullOrWhiteSpace(relativeTo) && _liveFrames.TryGetValue(relativeTo, out var parentHolder))
                                            {
                                                var px = Canvas.GetLeft(parentHolder);
                                                var py = Canvas.GetTop(parentHolder);
                                                double w = 0, h = 0;
                                                var b = parentHolder.Children.OfType<Border>().FirstOrDefault();
                                                if (b != null) { w = b.Width; h = b.Height; }
                                                var (cl, ct) = ClampToCanvas(px + x, py + y, w, h);
                                                Canvas.SetLeft(holder, cl);
                                                Canvas.SetTop(holder, ct);
                                            }
                                            else
                                            {
                                                double w = 0, h = 0;
                                                var b = holder.Children.OfType<Border>().FirstOrDefault();
                                                if (b != null) { w = b.Width; h = b.Height; }
                                                var (cl, ct) = ClampToCanvas(x, y, w, h);
                                                Canvas.SetLeft(holder, cl);
                                                Canvas.SetTop(holder, ct);
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "CreateTexture", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                                {
                                    // CreateTexture|parentName|childName
                                    var parentName = parts[1];
                                    var childName = parts[2];
                                    try
                                    {
                                        if (_liveFrames.TryGetValue(parentName, out var holder))
                                        {
                                            var border = holder.Children.OfType<Border>().FirstOrDefault();
                                            Grid? grid = border?.Child as Grid;
                                            if (grid != null)
                                            {
                                                // find or create inner canvas
                                                var inner = grid.Children.OfType<Canvas>().FirstOrDefault(c => c.Name == "InnerCanvas") as Canvas;
                                                if (inner == null)
                                                {
                                                    inner = new Canvas { Name = "InnerCanvas" };
                                                    grid.Children.Add(inner);
                                                }
                                                var img = new Image { Width = 64, Height = 64 };
                                                inner.Children.Add(img);
                                                Canvas.SetLeft(img, 0);
                                                Canvas.SetTop(img, 0);
                                                _liveObjects[childName] = img;
                                                AppendToEmuConsole($"Created texture '{childName}' under '{parentName}'");
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "SetTexture", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                                {
                                    var obj = parts[1];
                                    var tex = parts[2];
                                    try
                                    {
                                            if (_liveObjects.TryGetValue(obj, out var ctl) && ctl is Image img)
                                        {
                                            string path = tex;
                                            if (!File.Exists(path))
                                            {
                                                var found = FindTextureForName(tex);
                                                if (!string.IsNullOrEmpty(found)) path = found;
                                            }
                                            if (File.Exists(path))
                                            {
                                                try { img.Source = new Bitmap(path); }
                                                catch { }
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "CreateFontString", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                                {
                                    var parentName = parts[1];
                                    var childName = parts[2];
                                    try
                                    {
                                        if (_liveFrames.TryGetValue(parentName, out var holder))
                                        {
                                            var border = holder.Children.OfType<Border>().FirstOrDefault();
                                            Grid? grid = border?.Child as Grid;
                                            if (grid != null)
                                            {
                                                var inner = grid.Children.OfType<Canvas>().FirstOrDefault(c => c.Name == "InnerCanvas") as Canvas;
                                                if (inner == null)
                                                {
                                                    inner = new Canvas { Name = "InnerCanvas" };
                                                    grid.Children.Add(inner);
                                                }
                                                var tb = new TextBlock { Text = "", Foreground = Brushes.White };
                                                inner.Children.Add(tb);
                                                Canvas.SetLeft(tb, 4);
                                                Canvas.SetTop(tb, 4);
                                                _liveObjects[childName] = tb;
                                                AppendToEmuConsole($"Created fontstring '{childName}' under '{parentName}'");
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "SetText", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                                {
                                    var obj = parts[1];
                                    var text = parts[2];
                                    try
                                    {
                                        if (_liveObjects.TryGetValue(obj, out var ctl) && ctl is TextBlock tb)
                                        {
                                            tb.Text = System.Uri.UnescapeDataString(text);
                                        }
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "LibDBIconRegister", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                                {
                                    var iconName = parts[1];
                                    try
                                    {
                                        // Attempt to attach a button to the Minimap module if present
                                        var mapHolder = _canvas?.Children.OfType<Canvas>().FirstOrDefault(c => string.Equals(c.Name, "mod_map_holder", StringComparison.OrdinalIgnoreCase));
                                        if (mapHolder != null)
                                        {
                                            var innerBorder = mapHolder.Children.OfType<Border>().FirstOrDefault();
                                            Grid? grid = innerBorder?.Child as Grid;
                                            var content = grid?.Children.OfType<Grid>().FirstOrDefault(x => x.Name == "ContentArea");
                                            if (content != null)
                                            {
                                                // ensure a host canvas for minimap buttons exists
                                                var host = content.Children.OfType<Canvas>().FirstOrDefault(c => c.Name == "MinimapButtonsHost");
                                                if (host == null)
                                                {
                                                    host = new Canvas { Name = "MinimapButtonsHost", IsHitTestVisible = true };
                                                    content.Children.Add(host);
                                                }

                                                double btnSize = 28;
                                                // query Lua for broker icon and stored minimapPos
                                                var (iconPath, posStr) = QueryMinimapBrokerInfo(iconName);
                                                Control createdCtl = null;
                                                Bitmap? bmp = null;
                                                if (!string.IsNullOrWhiteSpace(iconPath))
                                                {
                                                    try
                                                    {
                                                        // Try to load via TextureCache (handles BLP and common paths)
                                                        var td = TextureCache.LoadTexture(iconPath);
                                                        if (td != null) bmp = BitmapFromTextureData(td);
                                                    }
                                                    catch { }
                                                }

                                                if (bmp != null)
                                                {
                                                    var img = new Image { Source = bmp, Width = btnSize, Height = btnSize };
                                                    var btn = new Button { Width = btnSize, Height = btnSize, Padding = new Thickness(0), Content = img };
                                                    createdCtl = btn;
                                                    // click invokes broker OnClick
                                                    btn.Click += (_, __) =>
                                                    {
                                                        Task.Run(() =>
                                                        {
                                                            try
                                                            {
                                                                var safe = iconName.Replace("'", "\\'").Replace("\\", "\\\\");
                                                                KopiLuaRunner.TryRun($"if Flux and Flux._minimap_buttons and Flux._minimap_buttons['{safe}'] and type(Flux._minimap_buttons['{safe}'].broker.OnClick)=='function' then Flux._minimap_buttons['{safe}'].broker.OnClick('LeftButton') end");
                                                                Dispatcher.UIThread.Post(() => AppendToEmuConsole($"Minimap '{iconName}' clicked and invoked Lua broker."), Avalonia.Threading.DispatcherPriority.Background);
                                                            }
                                                            catch (Exception ex) { Dispatcher.UIThread.Post(() => AppendToEmuConsole("Minimap click invoke failed: " + ex.Message)); }
                                                        });
                                                    };
                                                }
                                                else
                                                {
                                                    // fallback to text button
                                                    var btn = new Button { Content = iconName, Width = btnSize, Height = btnSize };
                                                    createdCtl = btn;
                                                    btn.Click += (_, __) =>
                                                    {
                                                        Task.Run(() =>
                                                        {
                                                            try
                                                            {
                                                                var safe = iconName.Replace("'", "\\'").Replace("\\", "\\\\");
                                                                KopiLuaRunner.TryRun($"if Flux and Flux._minimap_buttons and Flux._minimap_buttons['{safe}'] and type(Flux._minimap_buttons['{safe}'].broker.OnClick)=='function' then Flux._minimap_buttons['{safe}'].broker.OnClick('LeftButton') end");
                                                                Dispatcher.UIThread.Post(() => AppendToEmuConsole($"Minimap '{iconName}' clicked and invoked Lua broker."), Avalonia.Threading.DispatcherPriority.Background);
                                                            }
                                                            catch (Exception ex) { Dispatcher.UIThread.Post(() => AppendToEmuConsole("Minimap click invoke failed: " + ex.Message)); }
                                                        });
                                                    };
                                                }

                                                // add to host
                                                int idx = host.Children.Count;
                                                host.Children.Add(createdCtl);

                                                // Position: if posStr contains an angle, place around minimap circle; otherwise arrange in a row
                                                if (double.TryParse(posStr, out var angleDeg))
                                                {
                                                    // Defer actual layout until host has measured
                                                    createdCtl.AttachedToVisualTree += (_, __) =>
                                                    {
                                                        try
                                                        {
                                                            var hb = host.Bounds;
                                                            var cx = hb.Width / 2.0;
                                                            var cy = hb.Height / 2.0;
                                                            var radius = Math.Max(10, Math.Min(hb.Width, hb.Height) / 2.0 - btnSize - 4);
                                                            var rad = angleDeg * Math.PI / 180.0;
                                                            var dx = Math.Cos(rad) * radius;
                                                            var dy = -Math.Sin(rad) * radius; // invert Y to match screen coord
                                                            var lx = cx + dx - btnSize / 2.0;
                                                            var ty = cy + dy - btnSize / 2.0;
                                                            Canvas.SetLeft(createdCtl, lx);
                                                            Canvas.SetTop(createdCtl, ty);
                                                        }
                                                        catch { }
                                                    };
                                                }
                                                else
                                                {
                                                    // row layout default
                                                    Canvas.SetLeft(createdCtl, 6 + idx * (btnSize + 6));
                                                    Canvas.SetTop(createdCtl, 6);
                                                }

                                                // record for later show/hide
                                                _liveObjects[iconName] = createdCtl;

                                                AppendToEmuConsole($"Registered minimap icon '{iconName}' (icon={iconPath}, pos={posStr})");
                                                continue;
                                            }
                                        }

                                        // fallback: create a top-level toolbar button if minimap module missing
                                        var fallbackBtn = new Button { Content = iconName, Width = 80, Height = 24 };
                                        _canvas?.Children.Add(fallbackBtn);
                                        var fx = 10 + _liveObjects.Count % 6 * 86;
                                        var fy = 10 + (_liveObjects.Count / 6) * 34;
                                        Canvas.SetLeft(fallbackBtn, fx);
                                        Canvas.SetTop(fallbackBtn, fy);
                                        _liveObjects[iconName] = fallbackBtn;
                                        AppendToEmuConsole($"Registered minimap icon (fallback) '{iconName}'");
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "LibDBIconShow", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                                {
                                    var iconName = parts[1];
                                    try
                                    {
                                        if (_liveObjects.TryGetValue(iconName, out var ctl)) ctl.IsVisible = true;
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "LibDBIconHide", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                                {
                                    var iconName = parts[1];
                                    try
                                    {
                                        if (_liveObjects.TryGetValue(iconName, out var ctl)) ctl.IsVisible = false;
                                    }
                                    catch { }
                                }
                                else if ((string.Equals(cmd, "ShowFrame", System.StringComparison.OrdinalIgnoreCase) || string.Equals(cmd, "ShowObject", System.StringComparison.OrdinalIgnoreCase)) && parts.Length >= 2)
                                {
                                    var obj = parts[1];
                                    try
                                    {
                                        if (_liveFrames.TryGetValue(obj, out var holder))
                                        {
                                            holder.IsVisible = true;
                                        }
                                        else if (_liveObjects.TryGetValue(obj, out var ctl)) ctl.IsVisible = true;
                                    }
                                    catch { }
                                }
                                else if ((string.Equals(cmd, "HideFrame", System.StringComparison.OrdinalIgnoreCase) || string.Equals(cmd, "HideObject", System.StringComparison.OrdinalIgnoreCase)) && parts.Length >= 2)
                                {
                                    var obj = parts[1];
                                    try
                                    {
                                        if (_liveFrames.TryGetValue(obj, out var holder))
                                        {
                                            holder.IsVisible = false;
                                        }
                                        else if (_liveObjects.TryGetValue(obj, out var ctl)) ctl.IsVisible = false;
                                    }
                                    catch { }
                                }
                                else if (string.Equals(cmd, "RegisterScript", System.StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                                {
                                    var frameName = parts[1];
                                    var what = parts[2];
                                    var hid = parts[3];
                                    try
                                    {
                                        var key = frameName + "|" + what;
                                        if (string.IsNullOrWhiteSpace(hid))
                                        {
                                            if (_registeredHandlers.ContainsKey(key)) _registeredHandlers.Remove(key);
                                            AppendToEmuConsole($"Unregistered script {what} for {frameName}");
                                        }
                                        else
                                        {
                                            _registeredHandlers[key] = hid;
                                            AppendToEmuConsole($"Registered script {what} for {frameName} -> handler {hid}");
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                });
                pollTimer.Start();
            }
            catch { }

            var applyBtn = this.FindControl<Button>("ApplyButton");
            if (applyBtn != null) applyBtn.Click += (_, __) => ApplyInspectorChanges();
            var deleteBtn = this.FindControl<Button>("DeleteButton");
            if (deleteBtn != null) deleteBtn.Click += (_, __) => DeleteSelectedFrame();

            var addBtn = this.FindControl<Button>("AddFrameButton");
            if (addBtn != null) addBtn.Click += (_, __) => AddFrame();

            var closeBtn = this.FindControl<Button>("CloseButton");
            if (closeBtn != null) closeBtn.Click += (_, __) => this.Close();
            
            var renderFramesBtn = this.FindControl<Button>("RenderFramesButton");
            if (renderFramesBtn != null)
            {
                renderFramesBtn.Click += (_, __) =>
                {
                    try
                    {
                        if (_canvas != null)
                        {
                            AppendToEmuConsole("Rendering frames from debug_frames.json...");
                            // If we have a known last-loaded addon, request its snapshot from the shim
                            string? framesJson = null;
                            if (!string.IsNullOrWhiteSpace(_lastLoadedAddonPath))
                            {
                                try
                                {
                                    var (ok, framesStr) = WoWApi.TryLoadAddonDirectory(_lastLoadedAddonPath);
                                    if (ok && !string.IsNullOrWhiteSpace(framesStr)) framesJson = framesStr;
                                }
                                catch { }
                            }

                            // Pass framesJson when available so textures and runtime anchors resolve relative to the addon
                            FrameRenderer.RenderFramesToCanvas(_canvas, _lastLoadedAddonPath, framesJson);
                            AppendToEmuConsole("Frame rendering complete");
                        }
                        else
                        {
                            AppendToEmuConsole("Error: Canvas not initialized");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendToEmuConsole($"Render frames failed: {ex.Message}");
                    }
                };
            }
        }

        // Clamp a desired left/top so the control's bounds remain fully inside the emulator canvas.
        private (double left, double top) ClampToCanvas(double left, double top, double width, double height)
        {
            try
            {
                if (_canvas == null) return (left, top);
                var cw = _canvas.Bounds.Width;
                var ch = _canvas.Bounds.Height;
                if (double.IsNaN(cw) || double.IsNaN(ch) || cw <= 0 || ch <= 0) return (left, top);
                // ensure values are finite
                if (double.IsNaN(left) || double.IsInfinity(left)) left = 0;
                if (double.IsNaN(top) || double.IsInfinity(top)) top = 0;
                if (double.IsNaN(width) || double.IsInfinity(width) || width < 0) width = 0;
                if (double.IsNaN(height) || double.IsInfinity(height) || height < 0) height = 0;

                var maxLeft = Math.Max(0, cw - width);
                var maxTop = Math.Max(0, ch - height);
                var nx = Math.Min(Math.Max(0, left), maxLeft);
                var ny = Math.Min(Math.Max(0, top), maxTop);
                return (nx, ny);
            }
            catch { return (left, top); }
        }

        // Public helper to load frames JSON (from serializer) into the emulator canvas.
        public bool LoadFramesFromJson(string framesJson, string addonName, string addonBasePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(framesJson)) return false;
                var infos = new System.Collections.Generic.List<FrameInfo>();
                using var doc = JsonDocument.Parse(framesJson);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array) return false;

                int idx = 0;
                foreach (var el in root.EnumerateArray())
                {
                    var name = el.GetProperty("name").GetString() ?? ("frame" + idx);
                    double x = el.TryGetProperty("x", out var px) && px.TryGetDouble(out var dx) ? dx : (80 + idx * 24);
                    double y = el.TryGetProperty("y", out var py) && py.TryGetDouble(out var dy) ? dy : (80 + idx * 18);
                    double w = el.TryGetProperty("w", out var pw) && pw.TryGetDouble(out var dw) ? dw : 220;
                    double h = el.TryGetProperty("h", out var ph) && ph.TryGetDouble(out var dh) ? dh : 140;
                    double scale = el.TryGetProperty("scale", out var ps) && ps.TryGetDouble(out var ds) ? ds : 1.0;

                    var frame = CreateEmuFrame(x, y, Math.Max(24, w), Math.Max(24, h), string.IsNullOrEmpty(name) ? addonName : name);

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

                // Resolve anchors / parenting
                ResolveAnchorsAndParenting(infos);
                return true;
            }
            catch (Exception ex)
            {
                AppendToEmuConsole("LoadFramesFromJson error: " + ex.Message);
                return false;
            }
        }

        // Module handling and console capture
        private void RegisterModuleHandlers()
        {
            _moduleHandlers["chat"] = enabled => { if (enabled) AddModuleFramesFromSample("chat"); else RemoveModuleVisual("chat"); };
            // unitframes: create native unit/raid/player/target frames with health/power
            _moduleHandlers["unitframes"] = enabled => { if (enabled) AddUnitFramesModule(); else { RemoveModuleVisual("unitframes"); StopUnitFrameTimer(); _unitBars.Clear(); } };
            _moduleHandlers["map"] = enabled => { if (enabled) AddModuleFramesFromSample("map"); else RemoveModuleVisual("map"); };
            _moduleHandlers["bags"] = enabled => { if (enabled) AddModuleFramesFromSample("bags"); else RemoveModuleVisual("bags"); };
        }

        private void ToggleModule(string name, bool enabled)
        {
            try
            {
                if (_moduleHandlers.TryGetValue(name.ToLowerInvariant(), out var act)) act(enabled);
            }
            catch { }
        }

        private void StopUnitFrameTimer()
        {
            try
            {
                if (_unitFrameTimer != null)
                {
                    _unitFrameTimer.Stop();
                    _unitFrameTimer = null;
                }
            }
            catch { }
        }

        private void EnableConsoleCapture(bool enable)
        {
            try
            {
                if (enable)
                {
                    if (_emuWriter == null)
                    {
                        _previousOut = Console.Out;
                        _previousErr = Console.Error;
                        _emuWriter = new EmuTextWriter(this);
                        Console.SetOut(_emuWriter);
                        Console.SetError(_emuWriter);
                        AppendToEmuConsole("Console capture enabled.");
                    }
                }
                else
                {
                    if (_previousOut != null) Console.SetOut(_previousOut);
                    if (_previousErr != null) Console.SetError(_previousErr);
                    _emuWriter = null;
                    AppendToEmuConsole("Console capture disabled.");
                }
            }
            catch (Exception ex)
            {
                AppendToEmuConsole("Failed to toggle console capture: " + ex.Message);
            }
        }

        private void RemoveModuleVisual(string name)
        {
            try
            {
                if (_canvas == null) return;
                // remove either exact-name or prefix (for module items named mod_<module>_...)
                var toRemove = _canvas.Children.Where(c =>
                {
                    var ctrl = c as Control;
                    if (ctrl == null) return false;
                    if (ctrl.Name == null) return false;
                    if (ctrl.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                    if (ctrl.Name.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)) return true;
                    if (ctrl.Name.StartsWith("mod_" + name, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }).ToList();

                foreach (var r in toRemove) try { _canvas.Children.Remove(r); } catch { }
            }
            catch { }
        }

        // Parse JSON produced by serializer/sample_frames.json into FrameInfo list
        private System.Collections.Generic.List<FrameInfo> ParseFramesFromJson(string framesStr)
        {
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
                        var name = el.TryGetProperty("name", out var pn) ? pn.GetString() ?? ("frame" + idx) : ("frame" + idx);
                        double x = el.TryGetProperty("x", out var px) && px.TryGetDouble(out var dx) ? dx : (80 + idx * 24);
                        double y = el.TryGetProperty("y", out var py) && py.TryGetDouble(out var dy) ? dy : (80 + idx * 18);
                        double w = el.TryGetProperty("w", out var pw) && pw.TryGetDouble(out var dw) ? dw : 220;
                        double h = el.TryGetProperty("h", out var ph) && ph.TryGetDouble(out var dh) ? dh : 140;
                        double scale = el.TryGetProperty("scale", out var ps) && ps.TryGetDouble(out var ds) ? ds : 1.0;

                        var frame = CreateEmuFrame(x, y, Math.Max(24, w), Math.Max(24, h), string.IsNullOrEmpty(name) ? ("frame" + idx) : name);

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
                        };

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
                AppendToEmuConsole("Failed to parse frames JSON for modules: " + ex.Message);
            }

            return infos;
        }

        private void AddModuleFramesFromSample(string module)
        {
            try
            {
                var samplePath = Path.Combine(AppContext.BaseDirectory ?? Environment.CurrentDirectory, "sample_frames.json");
                if (!File.Exists(samplePath))
                {
                    AppendToEmuConsole("No sample_frames.json found to drive modules.");
                    return;
                }
                var txt = File.ReadAllText(samplePath);
                if (string.IsNullOrWhiteSpace(txt)) return;
                var infos = ParseFramesFromJson(txt);

                // module keywords map
                var keywords = module.ToLowerInvariant() switch
                {
                    "chat" => new[] { "chat" },
                    "unitframes" => new[] { "player", "unit", "target", "party" },
                    "map" => new[] { "map", "minimap" },
                    "bags" => new[] { "bag", "container", "inventory" },
                    _ => new string[0]
                };

                // if no matches, add placeholders instead
                bool any = false;
                foreach (var fi in infos)
                {
                    if (string.IsNullOrWhiteSpace(fi.Name)) continue;
                    var lower = fi.Name.ToLowerInvariant();
                    if (keywords.Any(k => lower.Contains(k)))
                    {
                        // add to canvas but mark with module prefix
                        if (fi.Holder != null)
                        {
                            fi.Holder.Name = $"mod_{module}_{fi.Name}";
                            _canvas?.Children.Add(fi.Holder);
                            Canvas.SetLeft(fi.Holder, fi.RawX);
                            Canvas.SetTop(fi.Holder, fi.RawY);
                            any = true;
                        }
                    }
                }

                if (!any)
                {
                    // fallback placeholder visuals
                    switch (module.ToLowerInvariant())
                    {
                        case "chat": AddChatModule(); break;
                        case "unitframes": AddUnitFramesModule(); break;
                        case "map": AddMapModule(); break;
                        case "bags": AddBagsModule(); break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppendToEmuConsole("Error adding module frames from sample: " + ex.Message);
            }
        }

        private void AddChatModule()
        {
            try
            {
                if (_canvas == null) return;
                RemoveModuleVisual("chat");
                // create a draggable frame and insert chat UI into its content area
                var frame = CreateEmuFrame(12, 420, 360, 220, "Chat");
                frame.Name = "mod_chat_holder";
                var innerBorder = (frame as Canvas)?.Children.OfType<Border>().FirstOrDefault();
                if (innerBorder != null && innerBorder.Child is Grid g)
                {
                    var content = g.Children.OfType<Grid>().FirstOrDefault(x => x.Name == "ContentArea");
                    if (content != null)
                    {
                        // Compose chat background using nine-slice (use chatframe border + bg if available)
                        var baseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
                        string? topLeft = null, top = null, topRight = null, left = null, center = null, right = null, bottomLeft = null, bottom = null, bottomRight = null;
                        try
                        {
                            center = TextureManager.GetPath("chatframe", "bg");
                            left = TextureManager.GetPath("chatframe", "border");
                            top = TextureManager.GetPath("chatframe", "border");
                        }
                        catch { }

                        var bg = RenderHelpers.CreateNineSlice(topLeft, top, topRight, left, center, right, bottomLeft, bottom, bottomRight, cornerWidth: 8, cornerHeight: 8, width: 340, height: 170);
                        // place background behind content
                        var bgHost = new Canvas { IsHitTestVisible = false };
                        bgHost.Children.Add(bg);
                        content.Children.Add(bgHost);

                        // tab strip
                        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6,4,6,4) };
                        var tabLeft = TextureManager.GetPath("chatframe", "tab_left");
                        var tabMid = TextureManager.GetPath("chatframe", "tab_mid");
                        var tabRight = TextureManager.GetPath("chatframe", "tab_right");
                        var tabSelLeft = TextureManager.GetPath("chatframe", "tab_selected_left");
                        var tabSelMid = TextureManager.GetPath("chatframe", "tab_selected_mid");
                        var tabSelRight = TextureManager.GetPath("chatframe", "tab_selected_right");

                        string[] tabNames = new[] { "General", "Trade", "LocalDefense" };
                        foreach (var tname in tabNames)
                        {
                            // simple 3-slice for tab
                            var leftImg = tabLeft ?? tabSelLeft;
                            var midImg = tabMid ?? tabSelMid;
                            var rightImg = tabRight ?? tabSelRight;
                            var tabCtrl = RenderHelpers.CreateThreeSlice(leftImg, midImg, rightImg, leftWidth: 12, rightWidth: 12, height: 22);
                            var lbl = new TextBlock { Text = tname, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                            // overlay label onto tab
                            var wrapper = new Grid();
                            wrapper.Children.Add(tabCtrl);
                            wrapper.Children.Add(lbl);
                            tabs.Children.Add(wrapper);
                        }

                        // chat messages list
                        var list = new ListBox { Background = Brushes.Transparent, Foreground = Brushes.White, Margin = new Thickness(6,34,6,36) };
                        if (list.Items is System.Collections.IList il) { il.Add("[20:01] Welcome to the emulator chat."); il.Add("[20:02] Player1: Hello."); il.Add("[20:03] Player2: Hi!"); }

                        // input box for typing messages / slash commands
                        var inputBox = new TextBox { Width = 320, Height = 24, Margin = new Thickness(6, 0, 6, 6), Watermark = "Type message or /command and press Enter" };
                        inputBox.KeyDown += (s2, e2) =>
                        {
                            try
                            {
                                if (e2.Key == Avalonia.Input.Key.Enter)
                                {
                                    var txt = inputBox.Text ?? string.Empty;
                                    if (string.IsNullOrWhiteSpace(txt)) return;
                                    // If starts with '/', treat as slash command and forward to Lua
                                    if (txt.StartsWith("/"))
                                    {
                                        // Intercept local diagnostic command first
                                        if (txt.StartsWith("/fluxdiag", StringComparison.OrdinalIgnoreCase))
                                        {
                                            Task.Run(() =>
                                            {
                                                try
                                                {
                                                    var diag = WoWApi.DumpRuntimeDiagnostics();
                                                    Dispatcher.UIThread.Post(() =>
                                                    {
                                                        AppendToEmuConsole($"Runtime diagnostics (on-demand):\n{diag}");
                                                    }, Avalonia.Threading.DispatcherPriority.Background);
                                                }
                                                catch (Exception ex) { Dispatcher.UIThread.Post(() => AppendToEmuConsole("fluxdiag failed: " + ex.Message)); }
                                            });
                                        }
                                        else
                                        {
                                            Task.Run(() =>
                                            {
                                                try
                                                {
                                                    var ok = WoWApi.ExecuteSlashCommand(txt);
                                                    Dispatcher.UIThread.Post(() =>
                                                    {
                                                        AppendToEmuConsole($"Slash command '{txt}' executed -> {ok}");
                                                    }, Avalonia.Threading.DispatcherPriority.Background);
                                                }
                                                catch (Exception ex) { Dispatcher.UIThread.Post(() => AppendToEmuConsole("Slash cmd failed: " + ex.Message)); }
                                            });
                                        }
                                    }
                                    else
                                    {
                                        // Normal chat message: add to list visually
                                        try
                                        {
                                            if (list.Items is System.Collections.IList li) li.Add("[me] " + txt);
                                        }
                                        catch { }
                                    }
                                    inputBox.Text = string.Empty;
                                    e2.Handled = true;
                                }
                            }
                            catch { }
                        };

                        content.Children.Add(tabs);
                        content.Children.Add(list);
                        // place input after the list (lower margin of list reserved)
                        content.Children.Add(inputBox);
                    }
                }
                _canvas.Children.Add(frame);
            }
            catch { }
        }

        private void AddUnitFramesModule()
        {
            try
            {
                if (_canvas == null) return;
                RemoveModuleVisual("unitframes");

                var holder = new Canvas();
                holder.Name = "unitframes";

                // Player frame
                CreateUnitFrame(holder, "Player", 12, 12);

                // Target frame (top-right)
                CreateUnitFrame(holder, "Target", Math.Max(320, (_canvas.Bounds.Width > 400 ? _canvas.Bounds.Width - 200 : 320)), 12);

                // Target of Target near target
                CreateUnitFrame(holder, "TargetOfTarget", Math.Max(460, (_canvas.Bounds.Width > 460 ? _canvas.Bounds.Width - 80 : 460)), 12);

                // Raid frames grid (5x5)
                double startX = 12, startY = 120;
                int cols = 5, rows = 5; int count = cols * rows;
                double fw = 120, fh = 40, spacingX = 8, spacingY = 8;
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        var idx = r * cols + c + 1;
                        var name = "Raid" + idx;
                        var x = startX + c * (fw + spacingX);
                        var y = startY + r * (fh + spacingY);
                        CreateUnitFrame(holder, name, x, y, fw, fh);
                    }
                }

                _canvas.Children.Add(holder);

                // Skin raid frames: apply raid textures for hp bars and panel background where available
                try
                {
                    var hpFill = TextureManager.GetPath("raidframe", "hp_fill") ?? TextureManager.GetPath("raidframe", "hp_fill") ?? TextureManager.GetPath("raidframe", "UI-RAIDFRAME-HEALTHBAR");
                    var hpBg = TextureManager.GetPath("raidframe", "hp_bg") ?? TextureManager.GetPath("raidframe", "Raid-Bar-Hp-Bg.PNG") ?? TextureManager.GetPath("raidframe", "Raid-Bar-Hp-Bg");
                    foreach (var kv in _unitBars.Where(k => k.Key.StartsWith("Raid", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var name = kv.Key;
                            var (hpBar, powBar, hpText, portrait, castC, castText) = kv.Value;
                            if (!string.IsNullOrEmpty(hpBg))
                            {
                                var bmpBg = TextureManager.GetBitmap("raidframe", "hp_bg");
                                if (bmpBg != null) hpBar.Background = new ImageBrush(bmpBg) { Stretch = Stretch.Fill };
                            }
                            if (!string.IsNullOrEmpty(hpFill))
                            {
                                var bmpFill = TextureManager.GetBitmap("raidframe", "hp_fill");
                                if (bmpFill != null) hpBar.Foreground = new ImageBrush(bmpFill) { Stretch = Stretch.Fill };
                            }
                            // optional slight panel background behind portrait
                            try
                            {
                                var panelLeft = TextureManager.GetPath("raidframe", "panel_left");
                                var panelMid = TextureManager.GetPath("raidframe", "panel_middle");
                                var panelRight = TextureManager.GetPath("raidframe", "panel_right");
                                if (panelMid != null)
                                {
                                    var bg = RenderHelpers.CreateThreeSlice(panelLeft, panelMid, panelRight, leftWidth: 8, rightWidth: 8, height: portrait.Height + 8);
                                    // position behind portrait inside portrait's parent if possible
                                    try { (portrait.Parent as Panel)?.Children.Insert(0, bg); } catch { }
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
                catch { }

                // Start simulation timer
                if (_unitFrameTimer == null)
                {
                    _unitFrameTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(800), Avalonia.Threading.DispatcherPriority.Background, (s, e) =>
                    {
                        try
                        {
                            foreach (var kv in _unitBars.ToList())
                            {
                                var key = kv.Key;
                                var (hpBar, powBar, hpText, portrait, castBar, castText) = kv.Value;
                                // random walk for health/power
                                var newHp = Math.Max(0, Math.Min(100, hpBar.Value + (_rand.NextDouble() * 10.0 - 5.0)));
                                var newPow = Math.Max(0, Math.Min(100, powBar.Value + (_rand.NextDouble() * 8.0 - 4.0)));
                                hpBar.Value = newHp;
                                powBar.Value = newPow;
                                if (hpText != null) hpText.Text = ((int)newHp).ToString() + "/100";

                                // color hp bar based on percent
                                try
                                {
                                    if (newHp > 60) hpBar.Foreground = Brushes.LimeGreen;
                                    else if (newHp > 30) hpBar.Foreground = Brushes.Gold;
                                    else hpBar.Foreground = Brushes.IndianRed;
                                }
                                catch { }

                                // simple class/name color hints for title/portrait border
                                try
                                {
                                    var titleColor = Brushes.White;
                                    if (key.Equals("Player", StringComparison.OrdinalIgnoreCase)) titleColor = Brushes.LimeGreen;
                                    else if (key.StartsWith("Raid", StringComparison.OrdinalIgnoreCase)) titleColor = Brushes.LightSkyBlue;
                                    else if (key.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0) titleColor = Brushes.Gold;
                                    // apply to numeric label as a subtle hint
                                    if (hpText != null) hpText.Foreground = titleColor;
                                    // portrait edge: use a thin overlay
                                    if (portrait != null) portrait.BorderBrush = titleColor;
                                }
                                catch { }

                                // Castbar simulation: advance existing cast or occasionally start one
                                if (_castProgress.TryGetValue(key, out var prog))
                                {
                                    prog = Math.Min(100, prog + (_rand.NextDouble() * 35.0));
                                    _castProgress[key] = prog;
                                    if (castBar != null) castBar.Value = prog;
                                    if (castText != null) castText.Text = prog < 100 ? "Casting" : string.Empty;
                                    if (prog >= 100) _castProgress.Remove(key);
                                }
                                else
                                {
                                    // 8% chance to start a short cast
                                    if (_rand.NextDouble() < 0.08)
                                    {
                                        _castProgress[key] = 0.0;
                                        if (castText != null) castText.Text = "Casting Fireball";
                                        if (castBar != null) castBar.Value = 0;
                                    }
                                }
                            }
                        }
                        catch { }
                    });
                    _unitFrameTimer.Start();
                }
                AppendToEmuConsole("Added unit frames (player/target/raid). Simulation running.");
            }
            catch { }
        }

        private void CreateUnitFrame(Canvas holder, string name, double x, double y, double width = 160, double height = 44)
        {
            try
            {
                // create a proper emu frame so drag/resize handlers are present
                var frame = CreateEmuFrame(x, y, width, height, name);
                // give the frame a stable name so RemoveModuleVisual can find it
                frame.Name = "unitframes_" + name;

                // locate the inner content area created by CreateEmuFrame
                var innerBorder = (frame as Canvas)?.Children.OfType<Border>().FirstOrDefault();
                Avalonia.Controls.ProgressBar? hp = null;
                Avalonia.Controls.ProgressBar? pow = null;
                TextBlock? hpText = null;
                Border? portrait = null;
                RenderHelpers.CastBarControl? castBar = null;
                TextBlock? castText = null;
                TextBlock? hpNumeric = null;
                if (innerBorder != null && innerBorder.Child is Grid g)
                {
                    var content = g.Children.OfType<Grid>().FirstOrDefault(x => x.Name == "ContentArea");
                    if (content != null)
                    {
                        // layout: portrait on left, info stack on right
                        var layout = new Grid();
                        layout.ColumnDefinitions = new ColumnDefinitions("48, *");

                        // portrait placeholder (circular)
                        portrait = new Border
                        {
                            Width = 40,
                            Height = 40,
                            CornerRadius = new CornerRadius(20),
                            Background = (IBrush?)Application.Current?.FindResource("ModuleDecalBrush") ?? Brushes.DimGray,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                            Margin = new Thickness(6,6,6,6)
                        };

                        // right side stack with name, hp/power and castbar
                        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0,4,6,6) };
                        var nameTb = new TextBlock { Text = name, Margin = new Thickness(0,0,0,2), Foreground = Brushes.White, FontSize = 12 };
                        hp = new Avalonia.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 100, Height = 10, Margin = new Thickness(0, 0, 0, 2) };
                        // numeric HP label (current/max)
                        hpNumeric = new TextBlock { Text = "100/100", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0,0,0,4) };
                        pow = new Avalonia.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = 100, Height = 6, Margin = new Thickness(0, 0, 0, 4) };

                        // castbar area (use RenderHelpers.CastBarControl)
                        try
                        {
                            var fill = TextureManager.GetPath("castingbar", "border");
                            var spark = TextureManager.GetPath("castingbar", "spark");
                            castBar = new RenderHelpers.CastBarControl(fill, spark, height: 8);
                        }
                        catch { castBar = new RenderHelpers.CastBarControl(null, null, height: 8); }
                        castText = new TextBlock { Text = string.Empty, Foreground = Brushes.OrangeRed, FontSize = 11 };

                        rightStack.Children.Add(nameTb);
                        rightStack.Children.Add(hp);
                        rightStack.Children.Add(hpNumeric);
                        rightStack.Children.Add(pow);
                        rightStack.Children.Add(castBar as Control);
                        rightStack.Children.Add(castText);

                        Grid.SetColumn(portrait, 0);
                        Grid.SetColumn(rightStack, 1);
                        layout.Children.Add(portrait);
                        layout.Children.Add(rightStack);

                        // attempt to replace portrait placeholder with image if textures present
                        try
                        {
                            // prefer index-driven portrait
                            var ppath = TextureManager.GetPortraitPath(name);
                            if (!string.IsNullOrEmpty(ppath) && File.Exists(ppath))
                            {
                                try { var bmp = new Bitmap(ppath); portrait.Background = new ImageBrush(bmp) { Stretch = Avalonia.Media.Stretch.UniformToFill }; }
                                catch { }
                            }
                            else
                            {
                                var tex = FindTextureForName(name);
                                if (!string.IsNullOrEmpty(tex) && File.Exists(tex))
                                {
                                    try { var bmp = new Bitmap(tex); portrait.Background = new ImageBrush(bmp) { Stretch = Avalonia.Media.Stretch.UniformToFill }; }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        content.Children.Add(layout);

                        // assign numeric hpText reference to the hpNumeric label
                        hpText = hpNumeric;
                        // set portrait variable for capturing into dictionary below
                        // (we'll capture portrait and cast controls below)
                        // local variables hp, pow, hpText are set above
                        // but need to expose portrait, castBar, castText to store
                        // we'll capture via closure variables declared before
                        // (see storage below)
                        // nothing else here
                    }
                }

                // add to holder
                holder.Children.Add(frame);

                // record bars for updates directly from the variables we created
                try
                {
                    if (hp != null && pow != null)
                    {
                        try { (hp as Control).Name = "hpbar_" + name; } catch { }
                        // ensure fallback non-null values
                        var useHpText = hpText ?? new TextBlock { Text = ((int)hp.Value).ToString() + "/100", Foreground = Brushes.White, FontSize = 11 };
                        var usePortrait = portrait ?? new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Brushes.DimGray };
                        var useCastBar = castBar ?? new RenderHelpers.CastBarControl(null, null, height: 8);
                        var useCastText = castText ?? new TextBlock { Text = string.Empty, Foreground = Brushes.OrangeRed, FontSize = 11 };
                        _unitBars[name] = (hp, pow, useHpText, usePortrait, useCastBar, useCastText);
                    }
                }
                catch { }
            }
            catch { }
        }

        // Public API to update unit health/power programmatically
        public bool SetUnitHealth(string unitName, double percent)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(unitName)) return false;
                var key = unitName.Trim();
                if (_unitBars.TryGetValue(key, out var bars))
                {
                    bars.hp.Value = Math.Max(0, Math.Min(100, percent));
                    try { if (bars.hpText != null) bars.hpText.Text = ((int)bars.hp.Value).ToString() + "/100"; } catch { }
                    return true;
                }

                // If unit frame doesn't exist, try to create it under the unitframes holder
                var unitHolder = _canvas?.Children.OfType<Canvas>().FirstOrDefault(c => string.Equals(c.Name, "unitframes", StringComparison.OrdinalIgnoreCase));
                if (unitHolder != null)
                {
                    // attempt to place new frame near top-left of unitframes area
                    CreateUnitFrame(unitHolder, key, 12 + _unitBars.Count * 6, 12 + _unitBars.Count * 2);
                    if (_unitBars.TryGetValue(key, out var newBars))
                    {
                        newBars.hp.Value = Math.Max(0, Math.Min(100, percent));
                        return true;
                    }
                }

                return false;
            }
            catch { return false; }
        }

        public bool SetUnitPower(string unitName, double percent)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(unitName)) return false;
                var key = unitName.Trim();
                if (_unitBars.TryGetValue(key, out var bars))
                {
                    bars.pow.Value = Math.Max(0, Math.Min(100, percent));
                    return true;
                }

                var unitHolder = _canvas?.Children.OfType<Canvas>().FirstOrDefault(c => string.Equals(c.Name, "unitframes", StringComparison.OrdinalIgnoreCase));
                if (unitHolder != null)
                {
                    CreateUnitFrame(unitHolder, key, 12 + _unitBars.Count * 6, 12 + _unitBars.Count * 2);
                    if (_unitBars.TryGetValue(key, out var newBars))
                    {
                        newBars.pow.Value = Math.Max(0, Math.Min(100, percent));
                        return true;
                    }
                }

                return false;
            }
            catch { return false; }
        }

        private void AddMapModule()
        {
            try
            {
                if (_canvas == null) return;
                RemoveModuleVisual("map");
                var frame = CreateEmuFrame(Math.Max(200, (_canvas.Bounds.Width > 200 ? _canvas.Bounds.Width - 180 : 520)), 12, 180, 180, "Minimap");
                frame.Name = "mod_map_holder";
                var inner = (frame as Canvas)?.Children.OfType<Border>().FirstOrDefault();
                if (inner != null && inner.Child is Grid g)
                {
                    var content = g.Children.OfType<Grid>().FirstOrDefault(x => x.Name == "ContentArea");
                    if (content != null)
                    {
                        var mapRect = new Border { Background = Brushes.DarkSlateGray, CornerRadius = new CornerRadius(90), Width = 140, Height = 140, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                        content.Children.Add(mapRect);
                    }
                }
                _canvas.Children.Add(frame);
            }
            catch { }
        }

        // Try to query the Lua VM for minimap broker icon path and stored minimapPos.
        // Returns tuple (iconPath, posString) where either may be empty.
        private (string icon, string pos) QueryMinimapBrokerInfo(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return (string.Empty, string.Empty);
                var safe = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var lua = $@"local out = ''
local b = Flux and Flux._minimap_buttons and Flux._minimap_buttons['{safe}']
if b then
  local icon = ''
  if b.broker then icon = b.broker.icon or b.broker.iconfile or b.broker.texture or b.broker.Texture or '' end
  local pos = ''
  if b.db and b.db.minimapPos then pos = tostring(b.db.minimapPos) end
  out = icon .. '|' .. pos
end
return out";
                var (ok, res) = KopiLuaRunner.TryRun(lua);
                if (!ok || string.IsNullOrEmpty(res)) return (string.Empty, string.Empty);
                var parts = res.Split(new[] { '|' }, 2);
                var icon = parts.Length > 0 ? parts[0] : string.Empty;
                var pos = parts.Length > 1 ? parts[1] : string.Empty;
                return (icon ?? string.Empty, pos ?? string.Empty);
            }
            catch { return (string.Empty, string.Empty); }
        }

        // Convert a TextureData (RGBA bytes) into an Avalonia Bitmap via ImageSharp in-memory PNG.
        private Bitmap? BitmapFromTextureData(TextureData? td)
        {
            try
            {
                if (td == null || td.Rgba == null || td.Rgba.Length == 0) return null;
                int w = td.Width; int h = td.Height;
                var pixels = new Rgba32[w * h];
                var src = td.Rgba;
                for (int i = 0, j = 0; i < pixels.Length; i++, j += 4)
                {
                    var r = src[j]; var g = src[j + 1]; var b = src[j + 2]; var a = src[j + 3];
                    pixels[i] = new Rgba32(r, g, b, a);
                }
                using var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(pixels, w, h);
                using var ms = new MemoryStream();
                img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                ms.Position = 0;
                return new Bitmap(ms);
            }
            catch { return null; }
        }

        private void AddBagsModule()
        {
            try
            {
                if (_canvas == null) return;
                RemoveModuleVisual("bags");
                var frame = CreateEmuFrame(520, 420, 260, 200, "Bags");
                frame.Name = "mod_bags_holder";
                var inner = (frame as Canvas)?.Children.OfType<Border>().FirstOrDefault();
                if (inner != null && inner.Child is Grid g)
                {
                    var content = g.Children.OfType<Grid>().FirstOrDefault(x => x.Name == "ContentArea");
                    if (content != null)
                    {
                        var wrap = new Avalonia.Controls.WrapPanel { Margin = new Thickness(6), Orientation = Avalonia.Layout.Orientation.Horizontal };
                        for (int i = 0; i < 20; i++)
                        {
                            var slot = new Border { Background = Brushes.BurlyWood, Width = 40, Height = 40, Margin = new Thickness(4), CornerRadius = new CornerRadius(4) };
                            wrap.Children.Add(slot);
                        }
                        content.Children.Add(wrap);
                    }
                }
                _canvas.Children.Add(frame);
            }
            catch { }
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
                Background = (IBrush?)Application.Current?.FindResource("ModuleBackgroundBrush") ?? Brushes.DarkGray,
                BorderBrush = (IBrush?)Application.Current?.FindResource("ModuleEdgeBrush") ?? Brushes.DimGray,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6)
            };

            // header + content layout
            var grid = new Grid();
            grid.RowDefinitions = new RowDefinitions("Auto, *");

            var header = new Border
            {
                Background = (IBrush?)Application.Current?.FindResource("ModuleTitleBrush") ?? Brushes.DimGray,
                Height = 26,
                CornerRadius = new CornerRadius(4,4,0,0),
                Padding = new Thickness(6,2,6,2)
            };

            // slight gloss line under header
            var headerInner = new Grid();
            // header layout: optional icon + title
            headerInner.ColumnDefinitions = new ColumnDefinitions("20, *");
            var iconImg = new Image { Width = 16, Height = 16, Margin = new Thickness(0,0,6,0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            Grid.SetColumn(iconImg, 0);
            headerInner.Children.Add(iconImg);

            var title = new TextBlock { Classes = { "moduleTitle" }, Text = titleText ?? "Frame" };
            Grid.SetColumn(title, 1);
            headerInner.Children.Add(title);
            var gloss = new Border { Background = (IBrush?)Application.Current?.FindResource("ModuleGlossLine"), Height = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Margin = new Thickness(0,0,0,0) };
            headerInner.Children.Add(gloss);
            header.Child = headerInner;

            // try to load an icon for the module/title (search textures)
                        try
                        {
                            // prefer explicit index mappings
                            var iconPath = TextureManager.GetModuleIconPath(titleText ?? string.Empty);
                            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                            {
                                try { var bmp = new Bitmap(iconPath); var img = headerInner.Children.OfType<Image>().FirstOrDefault(); if (img != null) img.Source = bmp; } catch { }
                            }
                            else
                            {
                                var maybe = FindTextureForName(titleText ?? string.Empty);
                                if (!string.IsNullOrEmpty(maybe) && File.Exists(maybe))
                                {
                                    try { var bmp = new Bitmap(maybe); var img = headerInner.Children.OfType<Image>().FirstOrDefault(); if (img != null) img.Source = bmp; } catch { }
                                }
                            }
                        }
                        catch { }

            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var contentArea = new Grid { Name = "ContentArea", Background = (IBrush?)Application.Current?.FindResource("ModuleInnerBrush") ?? Brushes.Transparent };
            Grid.SetRow(contentArea, 1);
            grid.Children.Add(contentArea);

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
                var b = holder.Children.OfType<Border>().FirstOrDefault();
                double hbW = b?.Width ?? 0, hbH = b?.Height ?? 0;
                var (nLeft, nTop) = ClampToCanvas(startLeft + dx, startTop + dy, hbW, hbH);
                Canvas.SetLeft(holder, nLeft);
                Canvas.SetTop(holder, nTop);
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
                // invoke OnClick handler if registered
                try
                {
                    if (!string.IsNullOrWhiteSpace(titleText))
                    {
                        var key = titleText + "|OnClick";
                        if (_registeredHandlers.TryGetValue(key, out var hid) && !string.IsNullOrWhiteSpace(hid))
                        {
                            var luaName = titleText.Replace("\\", "\\\\").Replace("\"", "\\\"");
                            Task.Run(() =>
                            {
                                try { KopiLuaRunner.TryRun($"__flux_invoke_handler({hid}, \"{luaName}\", \"OnClick\")"); }
                                catch { }
                            });
                        }
                    }
                }
                catch { }
            };

            // (Enter/Leave events omitted for now)

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
                try
                {
                    var b = holder.Children.OfType<Border>().FirstOrDefault();
                    double w = b?.Width ?? 0, h = b?.Height ?? 0;
                    var (cl, ct) = ClampToCanvas(left, top, w, h);
                    Canvas.SetLeft(holder, cl);
                    Canvas.SetTop(holder, ct);
                }
                catch
                {
                    Canvas.SetLeft(holder, left);
                    Canvas.SetTop(holder, top);
                }
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
                // prefix with timestamp for easier per-line debugging
                var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var line = $"[{ts}] {message}";

                lock (_consoleLock)
                {
                    _consoleBuffer.AppendLine(line);
                    if (!_consoleFlushScheduled)
                    {
                        _consoleFlushScheduled = true;
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                lock (_consoleLock)
                                {
                                    if (_emuConsole != null)
                                    {
                                        _emuConsole.Text = _consoleBuffer.ToString();
                                        _emuConsole.CaretIndex = _emuConsole.Text?.Length ?? 0;
                                    }
                                    _consoleFlushScheduled = false;
                                }
                            }
                            catch { _consoleFlushScheduled = false; }
                        }, Avalonia.Threading.DispatcherPriority.Background);
                    }
                }
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
            private readonly System.Text.StringBuilder _local = new System.Text.StringBuilder();
            public EmuTextWriter(EmulatorWindow win) { _win = win; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void WriteLine(string? value)
            {
                if (value == null) value = string.Empty;
                lock (_local)
                {
                    _local.AppendLine(value);
                    FlushLocal();
                }
            }
            public override void Write(char value)
            {
                lock (_local)
                {
                    _local.Append(value);
                    if (value == '\n') FlushLocal();
                    else if (_local.Length > 4096) FlushLocal();
                }
            }
            public override void Write(string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                lock (_local)
                {
                    _local.Append(value);
                    if (value.IndexOf('\n') >= 0 || _local.Length > 4096) FlushLocal();
                }
            }

            private void FlushLocal()
            {
                try
                {
                    var s = _local.ToString();
                    _local.Clear();
                    if (!string.IsNullOrEmpty(s)) _win.AppendToEmuConsole(s.TrimEnd('\n','\r'));
                }
                catch { }
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
