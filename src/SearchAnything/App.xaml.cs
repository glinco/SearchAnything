using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SearchAnything;

public partial class App : Application
{
    private const string SingleInstanceName = "SearchAnything.SingleInstance";
    private const string AlreadyRunningSignalName = "SearchAnything.AlreadyRunning";

    private Mutex? _singleInstance;
    private EventWaitHandle? _alreadyRunningSignal;
    private HotkeyManager? _hotkey;
    private TrayIcon? _tray;
    private IReadOnlyList<OcrService> _ocr = Array.Empty<OcrService>();
    private OverlayWindow? _overlay;
    private Settings _settings = new();
    private Hotkey _currentHotkey = Hotkey.Default;
    private string _lastQuery = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // SearchAnything.exe --selftest <output.txt> <image.png> [query]
        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            if (e.Args.Length >= 3 && File.Exists(e.Args[2]))
                _ = RunSelfTestAsync(e.Args);
            else
                Shutdown(2);
            return;
        }

        _singleInstance = new Mutex(true, SingleInstanceName, out bool isFirst);
        if (!isFirst)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        // A bug in one search shouldn't kill the tray app.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            _tray?.Notify("Something went wrong: " + args.Exception.Message);
        };

        _settings = Settings.Load();
        _currentHotkey = Hotkey.TryParse(_settings.Hotkey, out var saved) ? saved : Hotkey.Default;

        // Refresh the registered path in case the .exe was moved since "Start with Windows" was turned on.
        if (StartWithWindows.IsEnabled)
            StartWithWindows.Set(true);

        _ocr = OcrService.CreateAll();
        _tray = new TrayIcon(
            onSearch: () => ToggleOverlay(fromTray: true),
            onChangeHotkey: ChangeHotkey,
            onStartWithWindowsChanged: StartWithWindows.Set,
            onExit: Shutdown);
        _tray.SetHotkeyText(_currentHotkey.ToString());
        _tray.SetStartWithWindows(StartWithWindows.IsEnabled);

        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => ToggleOverlay(fromTray: false);
        ListenForOtherInstances();

        if (_ocr.Count == 0)
            _tray.Notify("No OCR language is installed. Add a language pack with text recognition in Windows Settings > Language.");
        else if (!_hotkey.Register(_currentHotkey))
            _tray.Notify($"{_currentHotkey} is already used by another app. Right-click this icon to pick another shortcut.");
        else
            _tray.Notify($"Running in the tray. Press {_currentHotkey} to search your screen.");
    }

    /// <summary>Opens a search of the screen under the mouse, or closes the one that's open.</summary>
    private async void ToggleOverlay(bool fromTray)
    {
        if (_overlay != null)
        {
            _overlay.SafeClose();
            return;
        }
        if (_ocr.Count == 0)
        {
            _tray?.Notify("No OCR language is installed, so there is nothing to search with.");
            return;
        }

        // Let the tray menu disappear before taking the screenshot.
        if (fromTray)
            await Task.Delay(250);

        CapturedScreen shot;
        try
        {
            shot = ScreenCapture.Grab(ScreenCapture.MonitorUnderCursor());
        }
        catch (Exception ex)
        {
            _tray?.Notify("Couldn't capture the screen: " + ex.Message);
            return;
        }

        _overlay = new OverlayWindow(shot, _ocr, _lastQuery);
        _overlay.Closed += (_, _) =>
        {
            _lastQuery = _overlay?.Query ?? _lastQuery;
            _overlay = null;
        };
        _overlay.Show();
        _overlay.Activate();
    }

    private void ChangeHotkey()
    {
        if (_hotkey == null || _tray == null)
            return;

        // Release the current shortcut so pressing it in the dialog records it instead of opening a search.
        _hotkey.Unregister();
        var dialog = new HotkeyDialog(_currentHotkey);
        Hotkey? chosen = dialog.ShowDialog() == true ? dialog.Result : null;

        if (chosen is { } next && _hotkey.Register(next))
        {
            _currentHotkey = next;
            _settings.Hotkey = next.ToString();
            _settings.Save();
            _tray.SetHotkeyText(next.ToString());
            _tray.Notify($"Shortcut changed to {next}.");
            return;
        }
        if (chosen is { } taken)
            _tray.Notify($"{taken} is already used by another app. Keeping {_currentHotkey}.");
        _hotkey.Register(_currentHotkey);
    }

    /// <summary>Lets a second launch of the .exe tell this one to say "already running".</summary>
    private void ListenForOtherInstances()
    {
        _alreadyRunningSignal = new EventWaitHandle(false, EventResetMode.AutoReset, AlreadyRunningSignalName);
        ThreadPool.RegisterWaitForSingleObject(
            _alreadyRunningSignal,
            (_, _) => Dispatcher.InvokeAsync(() =>
                _tray?.Notify($"SearchAnything is already running. Press {_currentHotkey} to search your screen.")),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalRunningInstance()
    {
        if (EventWaitHandle.TryOpenExisting(AlreadyRunningSignalName, out var signal))
        {
            using (signal)
                signal.Set();
        }
    }

    private async Task RunSelfTestAsync(string[] args)
    {
        // The report holds every word the OCR read, so never replace an existing file with it.
        if (File.Exists(args[1]))
        {
            Shutdown(1);
            return;
        }

        var report = new StringBuilder();
        try
        {
            // Only an image the developer chose: the self-test never reads the live screen.
            var shot = ScreenCapture.Load(args[2]);
            string? query = args.Length >= 4 ? args[3] : null;
            report.AppendLine($"Image: {shot.Width}x{shot.Height}");

            // Each installed OCR language on its own, so their mistakes can be compared...
            foreach (var language in OcrService.AvailableLanguageTags)
            {
                var ocr = OcrService.Create(language) ?? throw new InvalidOperationException($"Can't create OCR for {language}.");
                var timer = Stopwatch.StartNew();
                var lines = await ocr.RecognizeAsync(shot);
                timer.Stop();
                var index = new SearchIndex(lines);
                var matches = query != null ? index.Find(query) : new();
                report.AppendLine($"{ocr.LanguageTag}: {lines.Count} lines, {index.WordCount} words, " +
                    $"{timer.ElapsedMilliseconds} ms, {matches.Count} matches");
                report.AppendLine("  text: " + index.AllText.Replace(Environment.NewLine, " | "));
            }

            // ...then all of them together, exactly as the app searches.
            var engines = OcrService.CreateAll();
            var combinedTimer = Stopwatch.StartNew();
            var readings = await Task.WhenAll(engines.Select(engine => engine.RecognizeAsync(shot)));
            combinedTimer.Stop();
            var combined = new CombinedIndex(readings);
            var found = query != null ? combined.Find(query) : new();
            report.AppendLine($"combined ({string.Join(" + ", engines.Select(e => e.LanguageTag))}): " +
                $"{combinedTimer.ElapsedMilliseconds} ms, {found.Count} matches");
            foreach (var match in found)
            {
                var b = match.Bounds;
                report.AppendLine($"    '{query}' at ({b.X:0},{b.Y:0},{b.Width:0}x{b.Height:0}) in \"{match.LineText}\"");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
        }
        try
        {
            using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(output);
            writer.Write(report.ToString());
        }
        catch (IOException)
        {
            // The file appeared since the check above; leave it alone.
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _alreadyRunningSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
