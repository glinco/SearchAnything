using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SearchAnything;

/// <summary>Full-screen frozen snapshot of one monitor with a find bar and highlighted matches.</summary>
public partial class OverlayWindow : Window
{
    // Measured: upscaling screen captures made OCR slower and slightly less accurate.
    private const double OcrScale = 1.0;
    private const double MarkPadding = 3;

    private static readonly Brush MatchStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)));
    private static readonly Brush CurrentStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00)));
    private static readonly Brush CurrentFill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x8A, 0x00)));
    private static readonly Brush StatusNormal = Freeze(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush StatusNone = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));

    private readonly CapturedScreen _shot;
    private readonly IReadOnlyList<OcrService> _ocr;
    private CombinedIndex? _index;
    private List<TextMatch> _matches = new();
    private int _current = -1;
    private bool _closing;

    internal string Query => SearchBox.Text;

    internal OverlayWindow(CapturedScreen shot, IReadOnlyList<OcrService> ocr, string initialQuery)
    {
        InitializeComponent();
        _shot = shot;
        _ocr = ocr;

        var image = BitmapSource.Create(shot.Width, shot.Height, 96, 96, PixelFormats.Bgra32, null, shot.Pixels, shot.Stride);
        image.Freeze();
        Shot.Source = image;
        Surface.Width = shot.Width;
        Surface.Height = shot.Height;
        Dim.Data = new RectangleGeometry(new Rect(0, 0, shot.Width, shot.Height));

        SearchBox.Text = initialQuery;
        UpdatePlaceholder();

        SourceInitialized += (_, _) => PlaceOnMonitor();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnMonitor);
        Loaded += async (_, _) =>
        {
            PlaceOnMonitor();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            SearchBox.Focus();
            SearchBox.SelectAll();
            await RecognizeAsync();
        };
        Deactivated += (_, _) => SafeClose();
        Closing += (_, _) => _closing = true;
        PreviewKeyDown += OnKey;
    }

    /// <summary>Covers the captured monitor exactly, in physical pixels (WPF units would be DPI-scaled).</summary>
    private void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = _shot.Bounds;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0);
    }

    private async Task RecognizeAsync()
    {
        Status.Text = "Reading text…";
        try
        {
            // Every language engine reads the same screenshot in parallel.
            var readings = await Task.WhenAll(_ocr.Select(engine => engine.RecognizeAsync(_shot, OcrScale)));
            _index = new CombinedIndex(readings);
        }
        catch (Exception ex)
        {
            Status.Text = "OCR failed";
            System.Diagnostics.Debug.WriteLine(ex);
            return;
        }
        RunSearch();
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        RunSearch();
    }

    private void UpdatePlaceholder() =>
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RunSearch()
    {
        if (_index == null)
            return;
        _matches = _index.Find(SearchBox.Text);
        _current = _matches.Count > 0 ? 0 : -1;
        Render();
    }

    private void Render()
    {
        Marks.Children.Clear();
        var full = new Rect(0, 0, _shot.Width, _shot.Height);

        for (int i = 0; i < _matches.Count; i++)
        {
            foreach (var rawBox in _matches[i].Boxes)
            {
                var box = Pad(rawBox);
                var mark = new Rectangle
                {
                    Width = box.Width,
                    Height = box.Height,
                    RadiusX = 3,
                    RadiusY = 3,
                    Stroke = i == _current ? CurrentStroke : MatchStroke,
                    StrokeThickness = i == _current ? 3 : 2,
                    Fill = i == _current ? CurrentFill : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Tag = i,
                };
                mark.MouseLeftButtonDown += (s, _) =>
                {
                    _current = (int)((FrameworkElement)s).Tag;
                    Render();
                };
                Canvas.SetLeft(mark, box.X);
                Canvas.SetTop(mark, box.Y);
                Marks.Children.Add(mark);
            }
        }

        // Spotlight: dim the whole screen except the matches. Overlapping holes are merged first,
        // because with the even-odd rule an area covered by two holes would turn dark again.
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(full));
        foreach (var hole in RectMerge.MergeOverlapping(_matches.SelectMany(m => m.Boxes).Select(Pad)))
            dim.Children.Add(new RectangleGeometry(hole, 3, 3));
        Dim.Data = dim;
        Dim.Opacity = SearchBox.Text.Trim().Length == 0 ? 0.25 : 0.55;

        UpdateStatus();
        KeepPanelOffCurrentMatch();
    }

    private void UpdateStatus()
    {
        Status.Foreground = StatusNormal;
        if (_index == null)
            Status.Text = "Reading text…";
        else if (SearchBox.Text.Trim().Length == 0)
            Status.Text = $"{_index.WordCount} words";
        else if (_matches.Count == 0)
        {
            Status.Text = "No matches";
            Status.Foreground = StatusNone;
        }
        else
            Status.Text = $"{_current + 1} of {_matches.Count}";
    }

    /// <summary>Moves the find bar between top and bottom so it never covers the current match.</summary>
    private void KeepPanelOffCurrentMatch()
    {
        if (_current < 0 || ActualWidth <= 0)
            return;
        double scale = ActualWidth / _shot.Width;
        var b = _matches[_current].Bounds;
        var match = new Rect(b.X * scale, b.Y * scale, b.Width * scale, b.Height * scale);

        double left = (ActualWidth - Panel.ActualWidth) / 2;
        double height = Panel.ActualHeight + 24;
        var topSpot = new Rect(left, Panel.Margin.Top - 12, Panel.ActualWidth, height);
        var bottomSpot = new Rect(left, ActualHeight - Panel.Margin.Bottom - Panel.ActualHeight - 12, Panel.ActualWidth, height);

        bool atTop = Panel.VerticalAlignment == VerticalAlignment.Top;
        if (atTop && topSpot.IntersectsWith(match) && !bottomSpot.IntersectsWith(match))
            Panel.VerticalAlignment = VerticalAlignment.Bottom;
        else if (!atTop && bottomSpot.IntersectsWith(match) && !topSpot.IntersectsWith(match))
            Panel.VerticalAlignment = VerticalAlignment.Top;
    }

    private void Step(int direction)
    {
        if (_matches.Count == 0)
            return;
        _current = (_current + direction + _matches.Count) % _matches.Count;
        Render();
    }

    private void OnBackgroundClick(object sender, MouseButtonEventArgs e) => SafeClose();

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                SafeClose();
                break;
            case Key.Enter:
            case Key.F3:
                e.Handled = true;
                Step(shift ? -1 : 1);
                break;
            case Key.C when ctrl && shift:
                e.Handled = true;
                if (_index != null)
                    Copy(_index.AllText, "Copied all text");
                break;
            case Key.C when ctrl && SearchBox.SelectionLength == 0:
                e.Handled = true;
                if (_current >= 0)
                    Copy(_matches[_current].LineText, "Copied line");
                break;
        }
    }

    private void Copy(string text, string confirmation)
    {
        try
        {
            // Screen text can include passwords or personal data, so keep it out of the Win+V
            // clipboard history and off the cloud clipboard that syncs to other devices.
            var data = new DataObject();
            data.SetText(text);
            data.SetData("CanIncludeInClipboardHistory", DwordZero());
            data.SetData("CanUploadToCloudClipboard", DwordZero());
            Clipboard.SetDataObject(data, copy: true);
            Status.Text = confirmation;
        }
        catch (COMException)
        {
            Status.Text = "Clipboard busy";
        }
    }

    private static MemoryStream DwordZero() => new(BitConverter.GetBytes(0));

    private Rect Pad(Rect box)
    {
        box.Inflate(MarkPadding, MarkPadding);
        box.Intersect(new Rect(0, 0, _shot.Width, _shot.Height));
        return box;
    }

    /// <summary>Closes once, whichever way it was triggered (Esc, click, hotkey, focus lost).</summary>
    internal void SafeClose()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
