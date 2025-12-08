using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Threading;

namespace FluxNew
{
    public static class RenderHelpers
    {
        // Create a simple 3-slice horizontal bar: left image (fixed), middle (stretched), right (fixed)
        public static Control CreateThreeSlice(string? leftPath, string? midPath, string? rightPath, double leftWidth = 12, double rightWidth = 12, double height = 24)
        {
            var grid = new Grid();
            grid.ColumnDefinitions = new ColumnDefinitions($"{leftWidth}, *, {rightWidth}");
            grid.Height = height;

            if (!string.IsNullOrEmpty(leftPath) && FileExists(leftPath))
            {
                var img = new Image { Source = TryBitmap(leftPath), Stretch = Stretch.Fill, Width = leftWidth, Height = height };
                Grid.SetColumn(img, 0);
                grid.Children.Add(img);
            }

            if (!string.IsNullOrEmpty(midPath) && FileExists(midPath))
            {
                var img = new Image { Source = TryBitmap(midPath), Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                Grid.SetColumn(img, 1);
                grid.Children.Add(img);
            }

            if (!string.IsNullOrEmpty(rightPath) && FileExists(rightPath))
            {
                var img = new Image { Source = TryBitmap(rightPath), Stretch = Stretch.Fill, Width = rightWidth, Height = height };
                Grid.SetColumn(img, 2);
                grid.Children.Add(img);
            }

            return grid;
        }

        // Create a 3x3 nine-slice grid from provided paths. If any path is null, that cell is left empty.
        public static Control CreateNineSlice(
            string? topLeft, string? top, string? topRight,
            string? left, string? center, string? right,
            string? bottomLeft, string? bottom, string? bottomRight,
            double cornerWidth = 12, double cornerHeight = 12, double width = 200, double height = 100)
        {
            var grid = new Grid();
            grid.RowDefinitions = new RowDefinitions($"{cornerHeight}, *, {cornerHeight}");
            grid.ColumnDefinitions = new ColumnDefinitions($"{cornerWidth}, *, {cornerWidth}");
            grid.Width = width;
            grid.Height = height;

            void Add(string? path, int row, int col, Stretch stretch = Stretch.Fill)
            {
                if (string.IsNullOrEmpty(path) || !FileExists(path)) return;
                var img = new Image { Source = TryBitmap(path), Stretch = stretch };
                Grid.SetRow(img, row);
                Grid.SetColumn(img, col);
                grid.Children.Add(img);
            }

            Add(topLeft, 0, 0, Stretch.None);
            Add(top, 0, 1, Stretch.Fill);
            Add(topRight, 0, 2, Stretch.None);

            Add(left, 1, 0, Stretch.Fill);
            Add(center, 1, 1, Stretch.Fill);
            Add(right, 1, 2, Stretch.Fill);

            Add(bottomLeft, 2, 0, Stretch.None);
            Add(bottom, 2, 1, Stretch.Fill);
            Add(bottomRight, 2, 2, Stretch.None);

            return grid;
        }

        // Simple castbar control with spark overlay. The control contains a ProgressBar (fill) and an Image for the spark.
        public class CastBarControl : Grid
        {
            private readonly ProgressBar _fill;
            private readonly Image _spark;
            private double _value = 0.0;

            public CastBarControl(string? fillPath = null, string? sparkPath = null, double height = 12)
            {
                RowDefinitions = new RowDefinitions("*");
                ColumnDefinitions = new ColumnDefinitions("*");
                Height = height;

                // Progress fill
                _fill = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = height, Margin = new Thickness(0) };
                Children.Add(_fill);

                // spark overlay on a canvas so it can be positioned freely
                var overlay = new Canvas { IsHitTestVisible = false };
                Children.Add(overlay);

                _spark = new Image { Width = height, Height = height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                overlay.Children.Add(_spark);

                if (!string.IsNullOrEmpty(sparkPath) && FileExists(sparkPath)) _spark.Source = TryBitmap(sparkPath);
                if (!string.IsNullOrEmpty(fillPath) && FileExists(fillPath))
                {
                    var bmp = TryBitmap(fillPath);
                    if (bmp != null)
                    {
                        _fill.Classes.Add("castbar-fill");
                        _fill.Background = new ImageBrush(bmp) { Stretch = Stretch.Fill };
                        _fill.Foreground = new SolidColorBrush(Colors.Transparent);
                    }
                }

                // reposition on size changed
                SizeChanged += (s, e) => UpdateSparkPosition();
            }

            public double Value
            {
                get => _value;
                set
                {
                    _value = Math.Max(0, Math.Min(100, value));
                    Dispatcher.UIThread.Post(() => { _fill.Value = _value; UpdateSparkPosition(); }, DispatcherPriority.Background);
                }
            }

            private void UpdateSparkPosition()
            {
                try
                {
                    var overlay = (Canvas)Children[1];
                    if (overlay == null) return;
                    var totalWidth = Bounds.Width;
                    var sparkW = _spark.DesiredSize.Width > 0 ? _spark.DesiredSize.Width : _spark.Width;
                    var pos = (totalWidth - sparkW) * (_value / 100.0);
                    Canvas.SetLeft(_spark, pos);
                    Canvas.SetTop(_spark, (Bounds.Height - _spark.Height) / 2);
                }
                catch { }
            }
        }

        // Helpers
        private static Bitmap? TryBitmap(string path)
        {
            try { return new Bitmap(path); } catch { return null; }
        }

        private static bool FileExists(string path)
        {
            try { return File.Exists(path); } catch { return false; }
        }
    }
}
