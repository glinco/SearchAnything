using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
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
    private const int ProgramRows = 8;

    private const string ScreenHint = "Enter next · Shift+Enter previous · Ctrl+C copy line · Ctrl+Shift+C copy all · Ctrl+Enter programs · Esc close";
    private const string ProgramHint = "↑↓ choose · Enter open · Tab drive · Ctrl+O show in folder · Ctrl+P add to Start menu · Ctrl+C copy path · Esc back";

    private static readonly Brush MatchStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)));
    private static readonly Brush CurrentStroke = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00)));
    private static readonly Brush CurrentFill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x8A, 0x00)));
    private static readonly Brush StatusNormal = Freeze(new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush StatusNone = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Brush RowPicked = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush RowFolder = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipOn = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xBE, 0x00)));
    private static readonly Brush ChipOnBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xBE, 0x00)));
    private static readonly Brush ChipOnText = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xBE, 0x00)));
    private static readonly Brush ChipOff = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipOffBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipOffText = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ChipCount = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)));

    private readonly CapturedScreen _shot;
    private readonly IReadOnlyList<OcrService> _ocr;
    private readonly ProgramCatalog _programs;
    private CombinedIndex? _index;
    private List<TextMatch> _matches = new();
    private int _current = -1;
    private bool _closing;

    private bool _programMode;
    private List<ProgramEntry> _programHits = new();
    private int _programPick;
    /// <summary>Only programs on this drive ("D:"), or null for all drives.</summary>
    private string? _driveFilter;
    /// <summary>The drive chips shown, in order: null ("All") first.</summary>
    private List<string?> _chipDrives = new();
    /// <summary>Until then, scan progress leaves the status alone so "Added to Start menu" can be read.</summary>
    private DateTime _noteUntil;
    private static readonly TimeSpan NoteFor = TimeSpan.FromSeconds(2.5);
    /// <summary>Icons for rows shown during this search only; dropped when it closes.</summary>
    private readonly Dictionary<string, ImageSource?> _icons = new();

    internal string Query => SearchBox.Text;

    internal OverlayWindow(CapturedScreen shot, IReadOnlyList<OcrService> ocr, string initialQuery, ProgramCatalog programs)
    {
        InitializeComponent();
        _shot = shot;
        _ocr = ocr;
        _programs = programs;
        Hint.Text = ScreenHint;

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
        Closed += (_, _) =>
        {
            _programs.Changed -= OnProgramsChanged;
            ForgetScreenshot();
        };
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
            // Closed while reading: the screenshot is already wiped, so don't keep its text either.
            if (_closing)
                return;
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
        if (_programMode)
            RunProgramSearch(keepPick: false);
        else
            RunSearch();
    }

    private void UpdatePlaceholder() =>
        Placeholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void RunSearch()
    {
        if (_index == null || _programMode)
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
        bool nothingOnScreen = _index != null && SearchBox.Text.Trim().Length > 0 && _matches.Count == 0;
        ProgramsButton.Visibility = nothingOnScreen ? Visibility.Visible : Visibility.Collapsed;

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

        if (_programMode)
        {
            OnProgramKey(e, ctrl);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                SafeClose();
                break;
            case Key.Enter when ctrl || ProgramsButton.Visibility == Visibility.Visible:
                e.Handled = true;
                EnterProgramMode();
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

    private void OnProgramsButton(object sender, RoutedEventArgs e)
    {
        EnterProgramMode();
        SearchBox.Focus();
    }

    /// <summary>Same bar, same query, but searching the programs on this PC's drives instead of the screen.</summary>
    private void EnterProgramMode()
    {
        if (_programMode)
            return;
        _programMode = true;
        _matches = new();
        _current = -1;
        Marks.Children.Clear();
        Dim.Data = new RectangleGeometry(new Rect(0, 0, _shot.Width, _shot.Height));
        Dim.Opacity = 0.7;
        ProgramsButton.Visibility = Visibility.Collapsed;
        Placeholder.Text = "Find a program…";
        Hint.Text = ProgramHint;
        ProgramList.Visibility = Visibility.Visible;

        _programs.Changed += OnProgramsChanged;
        _programs.Request();
        RunProgramSearch(keepPick: false);
    }

    private void ExitProgramMode()
    {
        _programMode = false;
        _programs.Changed -= OnProgramsChanged;
        _programHits = new();
        _icons.Clear();
        _driveFilter = null;
        _chipDrives = new();
        DriveChips.Children.Clear();
        DriveChips.Visibility = Visibility.Collapsed;
        ProgramList.Children.Clear();
        ProgramList.Visibility = Visibility.Collapsed;
        Placeholder.Text = "Find on screen…";
        Hint.Text = ScreenHint;
        if (_index != null)
            RunSearch();
        else
            Render();
    }

    /// <summary>Raised from the scan thread as programs are found.</summary>
    private void OnProgramsChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_programMode && !_closing)
            RunProgramSearch(keepPick: true);
    });

    /// <param name="keepPick">Keep the same program selected while new results stream in.</param>
    private void RunProgramSearch(bool keepPick)
    {
        var picked = keepPick && _programPick < _programHits.Count ? _programHits[_programPick].Path : null;
        var results = _programs.Find(SearchBox.Text, ProgramRows, _driveFilter);
        _programHits = results.Top.ToList();
        _programPick = Math.Max(0, picked == null ? 0 : _programHits.FindIndex(p => p.Path == picked));

        ShowDriveChips(results.Drives);
        ProgramList.Children.Clear();
        for (int i = 0; i < _programHits.Count; i++)
            ProgramList.Children.Add(ProgramRow(_programHits[i], i));

        // An update from the scan keeps a fresh confirmation; typing replaces it.
        if (keepPick && DateTime.UtcNow < _noteUntil)
            return;
        _noteUntil = default;
        Status.Foreground = StatusNormal;
        if (results.ScanningDrive != null)
            Status.Text = results.Known == 0 ? $"Scanning {results.ScanningDrive}…" : $"{results.Total} · scanning {results.ScanningDrive}…";
        else if (results.Known == 0)
            Status.Text = _programs.Drives().Any(d => d.Scan) ? "No programs found" : "No drives chosen";
        else if (results.Total == 0)
        {
            Status.Text = "No matches";
            Status.Foreground = StatusNone;
        }
        else
            Status.Text = results.Total == 1 ? "1 program" : $"{results.Total} programs";
    }

    /// <summary>
    /// "All 42 · C: 30 · D: 12": shown once matches are on more than one drive, or while narrowed to one,
    /// so there's always a way back to all of them.
    /// </summary>
    private void ShowDriveChips(IReadOnlyList<DriveCount> drives)
    {
        var counts = drives.ToList();
        if (_driveFilter != null && !counts.Any(d => d.Drive == _driveFilter))
            counts = counts.Append(new DriveCount(_driveFilter, 0)).OrderBy(d => d.Drive, StringComparer.Ordinal).ToList();

        DriveChips.Children.Clear();
        _chipDrives = new List<string?> { null };
        _chipDrives.AddRange(counts.Select(d => d.Drive));
        if (counts.Count < 2 && _driveFilter == null)
        {
            DriveChips.Visibility = Visibility.Collapsed;
            return;
        }
        DriveChips.Visibility = Visibility.Visible;
        DriveChips.Children.Add(DriveChip(null, "All", counts.Sum(d => d.Count)));
        foreach (var drive in counts)
            DriveChips.Children.Add(DriveChip(drive.Drive, drive.Drive, drive.Count));
    }

    private Border DriveChip(string? drive, string label, int count)
    {
        bool on = drive == _driveFilter;
        var text = new TextBlock { FontSize = 12, Foreground = on ? ChipOnText : ChipOffText, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(label));
        text.Inlines.Add(new System.Windows.Documents.Run($"  {count}") { Foreground = ChipCount });
        var chip = new Border
        {
            Child = text,
            Background = on ? ChipOn : ChipOff,
            BorderBrush = on ? ChipOnBorder : ChipOffBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            ToolTip = drive == null ? "Programs on all drives" : $"Only programs on {drive}",
        };
        chip.MouseLeftButtonUp += (_, _) =>
        {
            FilterDrive(drive);
            SearchBox.Focus();
        };
        return chip;
    }

    /// <summary>Tab and Shift+Tab move along the drive chips.</summary>
    private void StepDrive(int direction)
    {
        if (DriveChips.Visibility != Visibility.Visible || _chipDrives.Count < 2)
            return;
        int at = Math.Max(0, _chipDrives.IndexOf(_driveFilter));
        FilterDrive(_chipDrives[(at + direction + _chipDrives.Count) % _chipDrives.Count]);
    }

    private void FilterDrive(string? drive)
    {
        _driveFilter = drive;
        RunProgramSearch(keepPick: false);
    }

    private Border ProgramRow(ProgramEntry program, int index)
    {
        var icon = new Image { Source = IconFor(program.Path), Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) };
        var name = new TextBlock { Text = program.Name, FontSize = 14, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        var folder = new TextBlock { Text = program.Path, FontSize = 11, Foreground = RowFolder, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(folder);
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        var border = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Background = index == _programPick ? RowPicked : Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = program.Path,
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            _programPick = index;
            OpenProgram();
        };
        return border;
    }

    private void PickProgram(int direction)
    {
        if (_programHits.Count == 0)
            return;
        _programPick = (_programPick + direction + _programHits.Count) % _programHits.Count;
        for (int i = 0; i < ProgramList.Children.Count; i++)
            ((Border)ProgramList.Children[i]).Background = i == _programPick ? RowPicked : Brushes.Transparent;
    }

    private void OnProgramKey(KeyEventArgs e, bool ctrl)
    {
        var picked = _programPick < _programHits.Count ? _programHits[_programPick] : null;
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Enter when ctrl:
                e.Handled = true;
                ExitProgramMode();
                break;
            case Key.Down:
                e.Handled = true;
                PickProgram(1);
                break;
            case Key.Up:
                e.Handled = true;
                PickProgram(-1);
                break;
            case Key.Tab:
                e.Handled = true;
                StepDrive(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.Enter:
                e.Handled = true;
                OpenProgram();
                break;
            case Key.O when ctrl:
                e.Handled = true;
                if (picked != null)
                    ShowInFolder(picked);
                break;
            case Key.P when ctrl:
                e.Handled = true;
                if (picked != null)
                    AddToStartMenu(picked);
                break;
            case Key.C when ctrl && SearchBox.SelectionLength == 0:
                e.Handled = true;
                if (picked != null)
                    Copy(picked.Path, "Copied path");
                break;
        }
    }

    private void OpenProgram()
    {
        if (_programPick >= _programHits.Count)
            return;
        var program = _programHits[_programPick];
        try
        {
            Process.Start(new ProcessStartInfo(program.Path) { UseShellExecute = true, WorkingDirectory = program.Folder });
            SafeClose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Includes saying "No" to an administrator prompt, and programs that need files lost with the old C: drive.
            ShowProgramProblem("Couldn't open it");
        }
    }

    private void ShowInFolder(ProgramEntry program)
    {
        try
        {
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(explorer, $"/select,\"{program.Path}\"");
            SafeClose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowProgramProblem("Couldn't open folder");
        }
    }

    private void AddToStartMenu(ProgramEntry program)
    {
        try
        {
            Status.Foreground = StatusNormal;
            Status.Text = StartMenu.Add(program) ? "Added to Start menu" : "Already in Start menu";
            _noteUntil = DateTime.UtcNow + NoteFor;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            ShowProgramProblem("Couldn't add it");
        }
    }

    private void ShowProgramProblem(string message)
    {
        Status.Text = message;
        Status.Foreground = StatusNone;
        _noteUntil = DateTime.UtcNow + NoteFor;
    }

    private ImageSource? IconFor(string path)
    {
        if (_icons.TryGetValue(path, out var cached))
            return cached;
        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or COMException or Win32Exception)
        {
            // No icon: the row still works.
        }
        _icons[path] = image;
        return image;
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
        _noteUntil = DateTime.UtcNow + NoteFor;
    }

    private static MemoryStream DwordZero() => new(BitConverter.GetBytes(0));

    /// <summary>Wipes the captured pixels and OCR text, so they don't linger in memory (or a crash dump).</summary>
    private void ForgetScreenshot()
    {
        Shot.Source = null;
        Array.Clear(_shot.Pixels);
        _index = null;
        _matches = new();
        _programHits = new();
        _icons.Clear();
    }

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
