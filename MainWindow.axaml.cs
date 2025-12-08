using Avalonia.Controls;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;
using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using System;
using System.IO;
using System.Text;
using System.Linq;
using Avalonia.Threading;

namespace FluxNew;

public partial class MainWindow : Window
{
    private object? _editorInstance;
    private Type? _editorType;
    private object? _innerTextAreaInstance;
    private Type? _innerTextAreaType;
    private string? _currentFilePath;
    private bool _isDirty = false;
    // When we programmatically change selection in the FileList we suppress the SelectionChanged handler
    private bool _suppressFileListSelectionChanged = false;

    public MainWindow()
    {
        InitializeComponent();
        // Redirect console early so diagnostics appear in the Debug Console
        RedirectConsoleToDebug();
        TryInitializeEditor();
        TryWireFileList();
        WireToolbarAndShortcuts();
    }

    private void WireToolbarAndShortcuts()
    {
        try
        {
            var fileBtn = this.FindControl<Button>("FileButton");
            if (fileBtn != null) fileBtn.Click += async (_, __) => await OnFileMenuOpenAsync();
                var viewBtn = this.FindControl<Button>("ViewButton");
                if (viewBtn != null)
                {
                    viewBtn.Click += (s, e) =>
                    {
                        try
                        {
                            var menu = new ContextMenu();
                            var mi = new MenuItem { Header = "Open Emulator" };
                            mi.Click += (_, __) =>
                            {
                                try
                                {
                                    var emu = new EmulatorWindow();
                                    emu.Show();
                                }
                                catch (Exception ex) { AppendToConsole($"Opening emulator failed: {ex.Message}"); }
                            };
                            // Add the MenuItem into the ContextMenu's Items collection
                            var itemsColl = menu.Items;
                            if (itemsColl is System.Collections.IList il)
                            {
                                il.Add(mi);
                            }
                            // Prefer opening the ContextMenu with the target control to avoid internal null errors
                            try
                            {
                                var miOpen = menu.GetType().GetMethod("Open", new Type[] { viewBtn.GetType() });
                                if (miOpen != null)
                                {
                                    miOpen.Invoke(menu, new object[] { viewBtn });
                                }
                                else
                                {
                                    // Fallback: set PlacementTarget then call parameterless Open
                                    menu.PlacementTarget = viewBtn;
                                    var op = menu.GetType().GetMethod("Open", Type.EmptyTypes);
                                    op?.Invoke(menu, Array.Empty<object>());
                                }
                            }
                            catch (Exception ex)
                            {
                                AppendToConsole($"ContextMenu open failed: {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendToConsole($"View menu failed: {ex.Message}");
                        }
                    };
                }

            // Key shortcut: Ctrl+S to save
            this.KeyDown += async (s, e) =>
            {
                try
                {
                    if (e.Key == Avalonia.Input.Key.S && (e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) == Avalonia.Input.KeyModifiers.Control)
                    {
                        e.Handled = true;
                        await SaveCurrentFileAsync();
                    }
                }
                catch { }
            };
        }
        catch (Exception ex)
        {
            AppendToConsole($"WireToolbar failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task OnFileMenuOpenAsync()
    {
        try
        {
            var dlg = new OpenFileDialog();
            dlg.Title = "Open file";
            dlg.AllowMultiple = false;
            var res = await dlg.ShowAsync(this);
            if (res != null && res.Length > 0)
            {
                var path = res[0];
                LoadFileToEditor(path);
            }
        }
        catch (Exception ex)
        {
            AppendToConsole($"Open dialog failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task SaveCurrentFileAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                await SaveCurrentFileAsAsync();
                return;
            }

            var content = GetEditorText();
            if (content == null) { AppendToConsole("No editor content to save"); return; }
            File.WriteAllText(_currentFilePath, content, Encoding.UTF8);
            _isDirty = false;
            AppendToConsole($"Saved: {_currentFilePath}");
        }
        catch (Exception ex)
        {
            AppendToConsole($"Save failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task SaveCurrentFileAsAsync()
    {
        try
        {
            var dlg = new SaveFileDialog();
            dlg.Title = "Save file as";
            var path = await dlg.ShowAsync(this);
            if (!string.IsNullOrEmpty(path))
            {
                var content = GetEditorText();
                if (content == null) { AppendToConsole("No editor content to save"); return; }
                File.WriteAllText(path, content, Encoding.UTF8);
                _currentFilePath = path;
                _isDirty = false;
                AppendToConsole($"Saved: {path}");
            }
        }
        catch (Exception ex)
        {
            AppendToConsole($"SaveAs failed: {ex.Message}");
        }
    }

    private string? GetEditorText()
    {
        try
        {
            if (_editorInstance == null) return null;
            if (_editorInstance is TextBox tb) return tb.Text;
            // reflection fallbacks
            var tprop = _editorType?.GetProperty("Text");
            if (tprop != null) return tprop.GetValue(_editorInstance)?.ToString();
            var docProp = _editorType?.GetProperty("Document");
            if (docProp != null)
            {
                var doc = docProp.GetValue(_editorInstance);
                if (doc != null)
                {
                    var txtProp = doc.GetType().GetProperty("Text");
                    if (txtProp != null) return txtProp.GetValue(doc)?.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    // Find a descendant visual of a given type using reflection (avoids direct IVisual dependency)
    private static object? FindDescendantOfType(object? root, Type desired)
    {
        if (root == null) return null;
        try
        {
            if (desired.IsAssignableFrom(root.GetType())) return root;
            var prop = root.GetType().GetProperty("VisualChildren");
            if (prop == null) prop = root.GetType().GetProperty("Children");
            if (prop != null)
            {
                var children = prop.GetValue(root) as System.Collections.IEnumerable;
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        var found = FindDescendantOfType(child, desired);
                        if (found != null) return found;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private void TryInitializeEditor()
    {
        try
        {
            AppendToConsole("TryInitializeEditor: starting (builtin editor)");
            var host = this.FindControl<ContentControl>("EditorHost");
            AppendToConsole(host == null ? "EditorHost not found" : "EditorHost found");
            if (host == null) return;

            var editorGrid = new Grid();
            editorGrid.ColumnDefinitions = new ColumnDefinitions("48,*");

            // Gutter for line numbers (simple TextBlock showing numbers)
            var gutterBorder = new Border { Background = new SolidColorBrush(Color.Parse("#0F1113")), BorderBrush = new SolidColorBrush(Color.Parse("#1F2224")), BorderThickness = new Thickness(0,0,1,0) };
            var gutterList = new TextBlock { Name = "LineGutter", Background = Brushes.Transparent, Foreground = Brushes.Gray, Margin = new Thickness(4,6), TextAlignment = TextAlignment.Right };
            gutterBorder.Child = gutterList;
            editorGrid.Children.Add(gutterBorder);
            Grid.SetColumn(gutterBorder, 0);

            // Right side: overlay grid with highlight TextBlock and transparent TextBox on top
            var overlayGrid = new Grid();
            var highlightBlock = new TextBlock { Name = "HighlightBlock", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, FontSize = 14, FontFamily = new FontFamily("Consolas, Menlo, 'Courier New'"), IsHitTestVisible = false, Margin = new Thickness(6) };
            var editBox = new TextBox
            {
                Name = "EditorTextBox",
                AcceptsReturn = true,
                AcceptsTab = true,
                IsReadOnly = false,
                FontSize = 14,
                FontFamily = new FontFamily("Consolas, Menlo, 'Courier New'"),
                Background = Brushes.Transparent,
                Foreground = Brushes.Transparent, // make text invisible so highlighted layer shows
                CaretBrush = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(6)
            };

                overlayGrid.Children.Add(highlightBlock);
                overlayGrid.Children.Add(editBox);
            Grid.SetColumn(overlayGrid, 1);
            editorGrid.Children.Add(overlayGrid);

                // Hook events to update highlight and gutter
                editBox.TextChanged += (_, __) => UpdateHighlightAndGutter(editBox, highlightBlock, gutterList);

            // When focused, show the editable TextBox text (disable highlight overlay)
            editBox.GotFocus += (_, __) =>
            {
                try
                {
                    highlightBlock.IsVisible = false;
                    editBox.Foreground = Brushes.LightGray;
                }
                catch { }
            };
            // When focus is lost, re-enable highlight overlay and hide raw text
            editBox.LostFocus += (_, __) =>
            {
                try
                {
                    editBox.Foreground = Brushes.Transparent;
                    highlightBlock.IsVisible = true;
                    // refresh highlight
                    UpdateHighlightAndGutter(editBox, highlightBlock, gutterList);
                }
                catch { }
            };

            // Initial state: show highlight and keep TextBox text hidden
            editBox.Foreground = Brushes.Transparent;
            highlightBlock.IsVisible = true;
            UpdateHighlightAndGutter(editBox, highlightBlock, gutterList);

            // Try to find the internal ScrollViewer of the TextBox and sync scrolling
            try
            {
                var svObj = FindDescendantOfType(editBox, typeof(ScrollViewer));
                var sv = svObj as ScrollViewer;
                if (sv != null)
                {
                    // initial sync
                    var off = sv.Offset;
                    highlightBlock.RenderTransform = new TranslateTransform(0, -off.Y);
                    gutterList.Margin = new Thickness(4, 6 - off.Y);

                    // Poll the offset periodically and sync (avoids Subscribe overload issues)
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var lastY = sv.Offset.Y;
                            while (true)
                            {
                                var cur = sv.Offset;
                                if (cur.Y != lastY)
                                {
                                    lastY = cur.Y;
                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        try
                                        {
                                            highlightBlock.RenderTransform = new TranslateTransform(0, -cur.Y);
                                            gutterList.Margin = new Thickness(4, 6 - cur.Y);
                                        }
                                        catch { }
                                    });
                                }
                                await System.Threading.Tasks.Task.Delay(30).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }

            // Expose instances to fields so other methods can access them
            _editorInstance = editBox;
            _editorType = editBox.GetType();

            host.Content = editorGrid;
            AppendToConsole("Hosted built-in styled editor with gutter and highlighting overlay");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Editor initialization failed: {ex.Message}");
        }
    }

    private void TryWireFileList()
    {
        try
        {
            var fileList = this.FindControl<ListBox>("FileList");
            if (fileList == null) return;

            fileList.SelectionChanged += (s, e) =>
            {
                if (_suppressFileListSelectionChanged) return;
                var sel = fileList.SelectedItem;
                string? path = null;
                if (sel is ListBoxItem lbi) path = lbi.Content?.ToString();
                else path = sel?.ToString();
                if (string.IsNullOrWhiteSpace(path)) return;

                // normalize path: if contains slash, treat relative; otherwise search by filename
                string filePath = path.Replace('/', Path.DirectorySeparatorChar);
                string full = Path.Combine(Environment.CurrentDirectory, filePath);
                if (!File.Exists(full))
                {
                    // try search by file name
                    var name = Path.GetFileName(path);
                    var found = Directory.GetFiles(Environment.CurrentDirectory, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) full = found;
                }

                if (File.Exists(full))
                {
                    LoadFileToEditor(full);
                    AppendToConsole($"Opened: {full}");
                }
                else
                {
                    AppendToConsole($"File not found: {path}");
                }
            };
        }
        catch (Exception ex)
        {
            AppendToConsole($"FileList wiring failed: {ex.Message}");
        }
    }

    private void LoadFileToEditor(string fullPath)
    {
        try
        {
            var code = File.ReadAllText(fullPath);

            // Ensure UI thread for editor updates
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    bool wrote = false;

                    if (_editorInstance == null || _editorType == null)
                    {
                        AppendToConsole("No editor instance available — will try fallback TextBox");
                    }
                    else
                    {
                        var textProp = _editorType.GetProperty("Text");
                        if (textProp != null && textProp.CanWrite)
                        {
                            try { textProp.SetValue(_editorInstance, code); AppendToConsole("Editor.Text set via Text property"); wrote = true; } catch (Exception ex) { AppendToConsole($"Editor.Text set failed: {ex.Message}"); }
                        }

                        // Try Document.Text pattern as well, or create a TextDocument if Document is null
                        var docProp = _editorType.GetProperty("Document");
                        if (docProp != null)
                        {
                            var doc = docProp.GetValue(_editorInstance);
                            if (doc != null)
                            {
                                try
                                {
                                    var docTextProp = doc.GetType().GetProperty("Text");
                                    if (docTextProp != null && docTextProp.CanWrite)
                                    {
                                        docTextProp.SetValue(doc, code);
                                        AppendToConsole($"Editor.Document.Text set (length={code.Length})");
                                        wrote = true;
                                    }
                                }
                                catch (Exception ex) { AppendToConsole($"Setting existing Document.Text failed: {ex.Message}"); }
                            }
                            else
                            {
                                // Try creating a TextDocument reflectively and assign it
                                try
                                {
                                    Type? textDocType = null;
                                    foreach (var a2 in AppDomain.CurrentDomain.GetAssemblies())
                                    {
                                        try
                                        {
                                            var t = a2.GetTypes().FirstOrDefault(x => string.Equals(x.Name, "TextDocument", StringComparison.OrdinalIgnoreCase));
                                            if (t != null) { textDocType = t; break; }
                                        }
                                        catch { }
                                    }
                                    if (textDocType != null)
                                    {
                                        object? newDoc = null;
                                        var ctor = textDocType.GetConstructor(new Type[] { typeof(string) });
                                        if (ctor != null)
                                        {
                                            try { newDoc = ctor.Invoke(new object[] { code }); } catch { }
                                        }
                                        else
                                        {
                                            try
                                            {
                                                newDoc = Activator.CreateInstance(textDocType);
                                                var txtProp = textDocType.GetProperty("Text");
                                                if (txtProp != null && txtProp.CanWrite) txtProp.SetValue(newDoc, code);
                                            }
                                            catch { }
                                        }

                                        if (newDoc != null)
                                        {
                                            try
                                            {
                                                docProp.SetValue(_editorInstance, newDoc);
                                                AppendToConsole("Created and assigned new TextDocument to editor.Document");
                                                wrote = true;
                                            }
                                            catch (Exception ex)
                                            {
                                                AppendToConsole($"Assigning new TextDocument failed: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppendToConsole($"TextDocument creation attempt failed: {ex.Message}");
                                }
                            }
                        }

                        // Also try inner text area document if available
                        if (!wrote && _innerTextAreaInstance != null && _innerTextAreaType != null)
                        {
                            try
                            {
                                var innerDocProp = _innerTextAreaType.GetProperty("Document");
                                if (innerDocProp != null)
                                {
                                    var innerDoc = innerDocProp.GetValue(_innerTextAreaInstance);
                                    if (innerDoc != null)
                                    {
                                        var docTextProp2 = innerDoc.GetType().GetProperty("Text");
                                        if (docTextProp2 != null && docTextProp2.CanWrite)
                                        {
                                            docTextProp2.SetValue(innerDoc, code);
                                            AppendToConsole($"Inner.Document.Text set (length={code.Length})");
                                            wrote = true;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                AppendToConsole($"Inner document set failed: {ex.Message}");
                            }
                        }

                        if (!wrote)
                        {
                            // Fallback: look for a method named SetText or Load
                            var setTextMethod = _editorType.GetMethod("SetText") ?? _editorType.GetMethod("Load");
                            if (setTextMethod != null)
                            {
                                try { setTextMethod.Invoke(_editorInstance, new object[] { code }); AppendToConsole("Editor text set via method fallback"); wrote = true; } catch (Exception ex) { AppendToConsole($"SetText/Load invoke failed: {ex.Message}"); }
                            }
                        }
                    }

                    // Force visual refresh and caret reset
                    try
                    {
                        if (_innerTextAreaInstance != null)
                        {
                            try { _innerTextAreaType.GetMethod("InvalidateVisual")?.Invoke(_innerTextAreaInstance, null); } catch { }
                            try
                            {
                                var tvProp = _innerTextAreaType.GetProperty("TextView") ?? _innerTextAreaType.GetProperty("TextViewCore");
                                if (tvProp != null)
                                {
                                    var tv = tvProp.GetValue(_innerTextAreaInstance);
                                    if (tv != null)
                                    {
                                        tv.GetType().GetMethod("InvalidateVisual")?.Invoke(tv, null);
                                        tv.GetType().GetMethod("InvalidateArrange")?.Invoke(tv, null);
                                    }
                                }
                            }
                            catch { }
                            AppendToConsole("Called inner.InvalidateVisual()/TextView invalidation attempts");
                        }
                    }
                    catch (Exception ex) { AppendToConsole($"Inner InvalidateVisual failed: {ex.Message}"); }

                    try
                    {
                        var ctrl = this.FindControl<ContentControl>("EditorHost");
                        if (ctrl != null)
                        {
                            var parent = ctrl.Content as Control;
                            parent?.InvalidateArrange();
                            parent?.InvalidateMeasure();
                            AppendToConsole("Called parent.InvalidateArrange/Measure");
                        }
                    }
                    catch { }

                    // Ultimate fallback: if nothing wrote, replace host with a plain TextBox editor so user can edit now
                    if (!wrote)
                    {
                        try
                        {
                            var hostCtrl = this.FindControl<ContentControl>("EditorHost");
                            if (hostCtrl != null)
                            {
                                var tb = new TextBox()
                                {
                                    Text = code,
                                    AcceptsReturn = true,
                                    AcceptsTab = true,
                                    IsReadOnly = false
                                };
                                hostCtrl.Content = tb;
                                AppendToConsole("Fallback: hosted built-in TextBox editor (editable)");
                                wrote = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendToConsole($"Fallback TextBox creation failed: {ex.Message}");
                        }
                    }

                    if (!wrote)
                        AppendToConsole("No suitable editor text setter found after attempts");

                    // Update current file path and sync selection in the FileList (UI thread)
                    try
                    {
                        _currentFilePath = fullPath;
                        _isDirty = false;
                        var fileList = this.FindControl<ListBox>("FileList");
                        if (fileList != null)
                        {
                            try
                            {
                                object? match = null;
                                var cwd = Environment.CurrentDirectory;
                                foreach (var it in fileList.Items)
                                {
                                    string? itemText = null;
                                    if (it is ListBoxItem li) itemText = li.Content?.ToString();
                                    else itemText = it?.ToString();
                                    if (string.IsNullOrWhiteSpace(itemText)) continue;

                                    string candidate = itemText.Replace('/', Path.DirectorySeparatorChar);
                                    string candidateFull = Path.Combine(cwd, candidate);
                                    if (!File.Exists(candidateFull))
                                    {
                                        var name = Path.GetFileName(itemText);
                                        var found = Directory.GetFiles(cwd, name, SearchOption.AllDirectories).FirstOrDefault();
                                        if (found != null) candidateFull = found;
                                    }

                                    try
                                    {
                                        if (File.Exists(candidateFull) && Path.GetFullPath(candidateFull).Equals(Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase))
                                        {
                                            match = it;
                                            break;
                                        }
                                    }
                                    catch { }
                                }

                                _suppressFileListSelectionChanged = true;
                                try
                                {
                                    if (match != null)
                                    {
                                        fileList.SelectedItem = match;
                                    }
                                    else
                                    {
                                        // Add a relative path entry to the FileList and select it
                                        string rel = fullPath;
                                        if (rel.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
                                            rel = rel.Substring(cwd.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                        rel = rel.Replace(Path.DirectorySeparatorChar, '/');

                                        var newItem = new ListBoxItem { Content = rel };
                                        if (fileList.Items is System.Collections.IList il)
                                        {
                                            il.Add(newItem);
                                            fileList.SelectedItem = newItem;
                                        }
                                        else
                                        {
                                            fileList.SelectedItem = newItem;
                                        }
                                    }
                                }
                                finally { _suppressFileListSelectionChanged = false; }
                            }
                            catch (Exception ex) { AppendToConsole($"FileList sync failed: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendToConsole($"FileList sync exception: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    AppendToConsole($"Error setting editor text: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            AppendToConsole($"LoadFileToEditor failed: {ex.Message}");
        }
    }

    private void AppendToConsole(string message)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                var console = this.FindControl<TextBox>("DebugConsole");
                if (console != null)
                {
                    console.Text += message + Environment.NewLine;
                    console.CaretIndex = console.Text?.Length ?? 0;
                }
            });
        }
        catch { }
    }

    private void RedirectConsoleToDebug()
    {
        try
        {
            var writer = new DebugTextWriter(this);
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        catch { }
    }

    private void InspectAndEnableEditor(object editor, Type editorType, Control ctrl)
    {
        AppendToConsole($"Inspecting editor type: {editorType.FullName}");

        // List some properties and methods
        try
        {
            var props = editorType.GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
            AppendToConsole($"Editor properties: {string.Join(", ", props.Take(20))}{(props.Length>20?" ...":"")} (total {props.Length})");
        }
        catch { }
        try
        {
            var methods = editorType.GetMethods().Select(m => m.Name).Distinct().OrderBy(n => n).ToArray();
            AppendToConsole($"Editor methods: {string.Join(", ", methods.Take(20))}{(methods.Length>20?" ...":"")} (total {methods.Length})");
        }
        catch { }

        // Try to set common properties that may make the editor editable
        try
        {
            var isReadOnlyProp = editorType.GetProperty("IsReadOnly") ?? editorType.GetProperty("ReadOnly");
            if (isReadOnlyProp != null && isReadOnlyProp.CanWrite)
            {
                isReadOnlyProp.SetValue(editor, false);
                AppendToConsole("Set IsReadOnly/ReadOnly = false");
            }
        }
        catch (Exception ex) { AppendToConsole($"Setting IsReadOnly failed: {ex.Message}"); }

        try
        {
            var isEnabledProp = editorType.GetProperty("IsEnabled");
            if (isEnabledProp != null && isEnabledProp.CanWrite)
            {
                isEnabledProp.SetValue(editor, true);
                AppendToConsole("Set IsEnabled = true");
            }
        }
        catch (Exception ex) { AppendToConsole($"Setting IsEnabled failed: {ex.Message}"); }

        try
        {
            // Try to set Focusable if present
            var focusableProp = editorType.GetProperty("Focusable");
            if (focusableProp != null && focusableProp.CanWrite)
            {
                focusableProp.SetValue(editor, true);
                AppendToConsole("Set Focusable = true");
            }
        }
        catch { }

        // If there's a Document with a Text property, ensure it's writable and set caret
        try
        {
            var docProp = editorType.GetProperty("Document");
            if (docProp != null)
            {
                var doc = docProp.GetValue(editor);
                if (doc != null)
                {
                    var docTextProp = doc.GetType().GetProperty("Text");
                    AppendToConsole(docTextProp == null ? "Editor.Document.Text not found" : "Editor.Document.Text exists");
                }
            }
        }
        catch { }

        // Attempt to focus the control on UI thread
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // Prefer calling the parameterless Focus() directly
                    try
                    {
                        ctrl.Focus();
                        AppendToConsole("Called Focus() on editor control (direct)");
                    }
                    catch (Exception) { }

                    // Only invoke a reflection Focus() if it has no parameters
                    var focusMethod = ctrl.GetType().GetMethod("Focus");
                    if (focusMethod != null && focusMethod.GetParameters().Length == 0)
                    {
                        focusMethod.Invoke(ctrl, Array.Empty<object>());
                        AppendToConsole("Called Focus() on editor control (reflection)");
                    }

                    // Try to set caret position if property exists
                    try
                    {
                        var caretProp = editor.GetType().GetProperty("CaretOffset");
                        if (caretProp != null && caretProp.CanWrite)
                        {
                            caretProp.SetValue(editor, 0);
                            AppendToConsole("Set CaretOffset = 0");
                        }
                    }
                    catch (Exception ex) { AppendToConsole($"Setting CaretOffset failed: {ex.Message}"); }
                }
                catch (Exception ex) { AppendToConsole($"Focus failed: {ex.Message}"); }
            });
        }
        catch { }
        // Try to find inner TextArea/TextView and enable/focus it
        try
        {
            TryEnableInnerTextArea(editor, editorType);
        }
        catch (Exception ex) { AppendToConsole($"TryEnableInnerTextArea error: {ex.Message}"); }
    }

    private void TryEnableInnerTextArea(object editor, Type editorType)
    {
        try
        {
            // look for properties/fields with TextArea or TextView in the type name
            object? inner = null;
            var props = editorType.GetProperties();
            foreach (var p in props)
            {
                if (p.PropertyType.Name.IndexOf("TextArea", StringComparison.OrdinalIgnoreCase) >= 0 || p.PropertyType.Name.IndexOf("TextView", StringComparison.OrdinalIgnoreCase) >= 0 || p.Name.IndexOf("TextArea", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { inner = p.GetValue(editor); AppendToConsole($"Found inner via property {p.Name}: {p.PropertyType.FullName}"); break; } catch { }
                }
            }
            if (inner == null)
            {
                var fields = editorType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                foreach (var f in fields)
                {
                    if (f.FieldType.Name.IndexOf("TextArea", StringComparison.OrdinalIgnoreCase) >= 0 || f.FieldType.Name.IndexOf("TextView", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("textArea", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { inner = f.GetValue(editor); AppendToConsole($"Found inner via field {f.Name}: {f.FieldType.FullName}"); break; } catch { }
                    }
                }
            }

            if (inner == null)
            {
                AppendToConsole("No inner TextArea/TextView found via properties/fields");
                return;
            }

            var innerType = inner.GetType();
            _innerTextAreaInstance = inner;
            _innerTextAreaType = innerType;
            AppendToConsole($"Inner type: {innerType.FullName}");

            // Try common actions: Focus, IsReadOnly, IsEnabled, Caret property
            try
            {
                var isReadOnlyProp = innerType.GetProperty("IsReadOnly") ?? innerType.GetProperty("ReadOnly");
                if (isReadOnlyProp != null && isReadOnlyProp.CanWrite)
                {
                    isReadOnlyProp.SetValue(inner, false);
                    AppendToConsole("Set inner IsReadOnly = false");
                }
            }
            catch (Exception ex) { AppendToConsole($"Setting inner IsReadOnly failed: {ex.Message}"); }

            try
            {
                var isEnabledProp = innerType.GetProperty("IsEnabled");
                if (isEnabledProp != null && isEnabledProp.CanWrite)
                {
                    isEnabledProp.SetValue(inner, true);
                    AppendToConsole("Set inner IsEnabled = true");
                }
            }
            catch (Exception ex) { AppendToConsole($"Setting inner IsEnabled failed: {ex.Message}"); }

            try
            {
                var focusMethod = innerType.GetMethod("Focus");
                if (focusMethod != null && focusMethod.GetParameters().Length == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { focusMethod.Invoke(inner, Array.Empty<object>()); AppendToConsole("Called inner.Focus()"); } catch (Exception ex) { AppendToConsole($"inner.Focus() failed: {ex.Message}"); }
                    });
                }
            }
            catch (Exception ex) { AppendToConsole($"Calling inner.Focus failed: {ex.Message}"); }

            try
            {
                var caretProp = innerType.GetProperty("Caret");
                if (caretProp != null)
                {
                    var caret = caretProp.GetValue(inner);
                    if (caret != null)
                    {
                        var offsetProp = caret.GetType().GetProperty("Offset") ?? caret.GetType().GetProperty("Column");
                        if (offsetProp != null && offsetProp.CanWrite)
                        {
                            offsetProp.SetValue(caret, 0);
                            AppendToConsole("Set inner caret offset to 0");
                        }
                    }
                }
            }
            catch (Exception ex) { AppendToConsole($"Setting inner caret failed: {ex.Message}"); }

            // Try to invoke common methods that may force the caret/visual to appear and ensure focus
            try
            {
                var candidateMethods = new[] { "ShowCaret", "ScrollToCaret", "BringCaretToView", "ScrollTo", "EnsureCaretVisible", "RequestBringIntoView", "InvalidateArrange", "InvalidateMeasure" };
                foreach (var m in candidateMethods)
                {
                    try
                    {
                        var mi = innerType.GetMethod(m);
                        if (mi != null)
                        {
                            try { mi.Invoke(inner, Array.Empty<object>()); AppendToConsole($"Called inner.{m}()"); } catch (Exception ex) { AppendToConsole($"inner.{m}() invoke failed: {ex.Message}"); }
                        }
                    }
                    catch { }
                }

                // If there's a TextView property, try invoking methods on it too
                try
                {
                    var tvProp = innerType.GetProperty("TextView") ?? innerType.GetProperty("TextViewCore");
                    if (tvProp != null)
                    {
                        var tv = tvProp.GetValue(inner);
                        if (tv != null)
                        {
                            foreach (var m in new[] { "ShowCaret", "InvalidateVisual", "InvalidateArrange", "InvalidateMeasure" })
                            {
                                var mi2 = tv.GetType().GetMethod(m);
                                if (mi2 != null)
                                {
                                    try { mi2.Invoke(tv, Array.Empty<object>()); AppendToConsole($"Called inner.TextView.{m}()"); } catch (Exception ex) { AppendToConsole($"inner.TextView.{m}() failed: {ex.Message}"); }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                AppendToConsole($"Additional caret/visual calls failed: {ex.Message}");
            }

        }
        catch (Exception ex)
        {
            AppendToConsole($"TryEnableInnerTextArea exception: {ex.Message}");
        }
    }

    // Simple syntax highlighting + gutter update for the transparent-overlay TextBox approach
    private static readonly string[] LuaKeywords = new[] { "local","function","end","return","if","then","else","for","in","do","while","repeat","until","break","nil","true","false","and","or","not" };

    private void UpdateHighlightAndGutter(TextBox editor, TextBlock highlightBlock, TextBlock gutter)
    {
        try
        {
            var text = editor.Text ?? string.Empty;

            // Update gutter (line numbers)
            var lines = text.Split('\n');
            var nums = string.Join("\n", Enumerable.Range(1, Math.Max(1, lines.Length)).Select(i => i.ToString()));
            gutter.Text = nums;

            // Simple token-based highlighting: strings, numbers, keywords, comments (-- ...)
            highlightBlock.Inlines.Clear();

            // Regex to match strings, comments, numbers, identifiers
            var pattern = "(?<str>\".*?\"|'.*?')|(?<comment>--.*?$)|(?<number>\\b\\d+\\.?\\d*\\b)|(?<ident>[_a-zA-Z][_a-zA-Z0-9]*)";
            var rx = new Regex(pattern, RegexOptions.Singleline | RegexOptions.Multiline);
            int lastIndex = 0;
            foreach (Match m in rx.Matches(text))
            {
                if (m.Index > lastIndex)
                {
                    var plain = text.Substring(lastIndex, m.Index - lastIndex);
                    highlightBlock.Inlines.Add(new Run { Text = plain, Foreground = Brushes.LightGray });
                }

                if (m.Groups["str"].Success)
                {
                    highlightBlock.Inlines.Add(new Run { Text = m.Value, Foreground = Brushes.Orange });
                }
                else if (m.Groups["comment"].Success)
                {
                    highlightBlock.Inlines.Add(new Run { Text = m.Value, Foreground = Brushes.Green });
                }
                else if (m.Groups["number"].Success)
                {
                    highlightBlock.Inlines.Add(new Run { Text = m.Value, Foreground = Brushes.CadetBlue });
                }
                else if (m.Groups["ident"].Success)
                {
                    var id = m.Value;
                    if (LuaKeywords.Contains(id))
                        highlightBlock.Inlines.Add(new Run { Text = id, Foreground = Brushes.MediumPurple });
                    else
                        highlightBlock.Inlines.Add(new Run { Text = id, Foreground = Brushes.LightGray });
                }

                lastIndex = m.Index + m.Length;
            }
            if (lastIndex < text.Length)
            {
                highlightBlock.Inlines.Add(new Run { Text = text.Substring(lastIndex), Foreground = Brushes.LightGray });
            }
        }
        catch (Exception ex)
        {
            AppendToConsole($"Highlight update failed: {ex.Message}");
        }
    }

    private class DebugTextWriter : TextWriter
    {
        private readonly MainWindow _win;
        public DebugTextWriter(MainWindow win) { _win = win; }
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            _win.AppendToConsole(value ?? string.Empty);
        }
        public override void Write(char value)
        {
            _win.AppendToConsole(value.ToString());
        }
    }
}