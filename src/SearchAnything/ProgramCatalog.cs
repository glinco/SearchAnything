using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;

namespace SearchAnything;

/// <summary>What a program search found, and whether more may still arrive.</summary>
/// <param name="Drives">Matches per drive, before narrowing to one drive.</param>
internal sealed record ProgramResults(IReadOnlyList<ProgramEntry> Top, int Total, int Known, string? ScanningDrive,
    IReadOnlyList<DriveCount> Drives);

/// <summary>A drive that can hold programs, and whether it's scanned.</summary>
/// <param name="Drive">"D:"</param>
/// <param name="Label">The volume name ("Games"), or empty.</param>
/// <param name="Removable">A USB stick or memory card: not scanned unless the user turns it on.</param>
/// <param name="System">The drive Windows runs from.</param>
internal sealed record DriveChoice(string Drive, string Label, bool Removable, bool System, bool Scan);

/// <summary>
/// The programs on this PC's drives, kept in memory only. It scans in the background at low priority,
/// steps back when the PC is short on memory, and lets go of the list entirely when memory runs low.
/// </summary>
internal sealed class ProgramCatalog : IDisposable
{
    /// <summary>A hard ceiling, so even a drive full of tools can't grow the list past a few MB.</summary>
    public const int MaxPrograms = 20_000;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(300);

    private sealed class ScanRun
    {
        public readonly List<ProgramEntry> Found = new();
        public readonly CancellationTokenSource Cancel = new();
        public volatile bool Urgent;
        public volatile string? Drive;
        /// <summary>A drive was turned on after this scan picked its drives: scan again when it ends.</summary>
        public volatile bool Redo;
    }

    /// <summary>Thrown inside a scan to leave a drive that was just turned off.</summary>
    private sealed class DriveTurnedOff : Exception { }

    private readonly object _gate = new();
    private readonly Timer _memoryCheck;
    /// <summary>Drives the user turned on or off ("D:" → scan); every other drive follows the default.</summary>
    private readonly Dictionary<string, bool> _driveChoices;
    private List<ProgramEntry>? _ready;
    private DateTime _readyAt;
    private ScanRun? _run;

    /// <summary>Raised from a background thread whenever results change.</summary>
    public event Action? Changed;

    public bool KeepReady { get; private set; }

    /// <param name="driveChoices">Saved choices from the tray menu's "Drives to scan".</param>
    public ProgramCatalog(bool keepReady, IReadOnlyDictionary<string, bool>? driveChoices = null)
    {
        KeepReady = keepReady;
        _driveChoices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (drive, scan) in driveChoices ?? new Dictionary<string, bool>())
            _driveChoices[drive] = scan;
        // The first check also starts the first scan, a little after startup so it doesn't compete with sign-in.
        _memoryCheck = new Timer(_ => CheckMemory(), null, FirstCheck, CheckEvery);
    }

    public void SetKeepReady(bool keep)
    {
        KeepReady = keep;
        if (keep)
            StartScan(urgent: false);
        else
            Drop();
    }

    /// <summary>Someone is waiting for programs: start a scan if the list is missing or old, at full speed.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_run != null)
            {
                _run.Urgent = true;
                return;
            }
            if (_ready != null && DateTime.UtcNow - _readyAt < StaleAfter)
                return;
        }
        StartScan(urgent: true);
    }

    /// <summary>Called when a search closes: without "Keep program list ready", the list goes with it.</summary>
    public void ReleaseIfNotKept()
    {
        if (!KeepReady)
            Drop();
    }

    /// <param name="drive">Only programs on this drive ("D:"), or null for all.</param>
    public ProgramResults Find(string query, int limit, string? drive = null)
    {
        lock (_gate)
        {
            // While refreshing, keep showing the previous list; on the first scan, show what's found so far.
            var source = _ready ?? _run?.Found ?? new List<ProgramEntry>();
            var top = ProgramMatcher.Find(source, query, limit, drive, out int total, out var drives);
            return new ProgramResults(top, total, source.Count, _run?.Drive, drives);
        }
    }

    /// <summary>The drives that can hold programs right now (internal and USB), and whether each is scanned.</summary>
    public IReadOnlyList<DriveChoice> Drives()
    {
        var system = ProgramEntry.DriveOf(Environment.SystemDirectory);
        var drives = new List<DriveChoice>();
        foreach (var info in DriveInfo.GetDrives())
        {
            // Network drives are left out: slow to walk, and they're other people's programs.
            if (info.DriveType is not (DriveType.Fixed or DriveType.Removable))
                continue;
            string label;
            try
            {
                if (!info.IsReady)
                    continue;
                label = info.VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var drive = ProgramEntry.DriveOf(info.Name);
            bool removable = info.DriveType == DriveType.Removable;
            drives.Add(new DriveChoice(drive, label, removable, drive == system, IsScanned(drive, removable)));
        }
        return drives;
    }

    /// <summary>The drive choices to save: only drives the user turned on or off.</summary>
    public Dictionary<string, bool> DriveChoices()
    {
        lock (_gate)
            return new Dictionary<string, bool>(_driveChoices, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Turns scanning a drive on or off. Off forgets its programs at once; on scans again.</summary>
    public void SetDriveScanned(string drive, bool scan)
    {
        bool start;
        lock (_gate)
        {
            _driveChoices[drive] = scan;
            if (!scan)
            {
                _ready?.RemoveAll(p => string.Equals(p.Drive, drive, StringComparison.OrdinalIgnoreCase));
                _run?.Found.RemoveAll(p => string.Equals(p.Drive, drive, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // The list no longer covers every chosen drive, so the next search scans again.
                _readyAt = DateTime.MinValue;
                if (_run != null)
                    _run.Redo = true;
            }
            // Scan now if the list is kept, or a search is showing it; otherwise the next search does.
            start = scan && _run == null && (KeepReady || _ready != null);
        }
        if (start)
            StartScan(urgent: false);
        Changed?.Invoke();
    }

    private bool IsScanned(string drive, bool removable)
    {
        lock (_gate)
            return _driveChoices.TryGetValue(drive, out var scan) ? scan : !removable;
    }

    private void StartScan(bool urgent)
    {
        ScanRun run;
        lock (_gate)
        {
            if (_run != null)
                return;
            run = _run = new ScanRun { Urgent = urgent };
        }
        new Thread(() => Scan(run)) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Program scan" }.Start();
    }

    private void Scan(ScanRun run)
    {
        var cancel = run.Cancel.Token;
        bool background = !run.Urgent;
        if (background)
            NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);
        var sinceReport = Stopwatch.StartNew();
        bool again = false;
        try
        {
            foreach (var choice in DrivesToScan())
            {
                run.Drive = choice.Drive;
                try
                {
                    ProgramScanner.Scan(choice.Drive + "\\", found: entry =>
                    {
                        lock (_gate)
                        {
                            if (run.Found.Count < MaxPrograms && IsScanned(choice.Drive, choice.Removable))
                                run.Found.Add(entry);
                        }
                    }, eachFolder: () =>
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (!IsScanned(choice.Drive, choice.Removable))
                            throw new DriveTurnedOff();
                        if (background && run.Urgent)
                        {
                            // Someone opened the program search: stop being polite about disk and CPU.
                            NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_END);
                            Thread.CurrentThread.Priority = ThreadPriority.Normal;
                            background = false;
                        }
                        // A background scan waits while the PC is short on memory.
                        while (!run.Urgent && MemoryGuard.LoadPercent >= MemoryGuard.TightPercent)
                        {
                            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                            cancel.ThrowIfCancellationRequested();
                        }
                        if (sinceReport.Elapsed > ReportEvery)
                        {
                            sinceReport.Restart();
                            Changed?.Invoke();
                        }
                    });
                }
                catch (DriveTurnedOff)
                {
                    // Turned off in the tray menu while being scanned: move on to the next drive.
                }
                catch (IOException)
                {
                    // Unplugged or ejected while being scanned.
                }
            }
            lock (_gate)
            {
                if (_run == run)
                {
                    _ready = run.Found;
                    // A drive turned on mid-scan was missed, so this list is already out of date.
                    _readyAt = run.Redo ? DateTime.MinValue : DateTime.UtcNow;
                    _run = null;
                    again = run.Redo && (KeepReady || run.Urgent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dropped: whoever cancelled already cleared _run.
        }
        finally
        {
            run.Drive = null;
            lock (_gate)
            {
                // Also covers an unexpected error, so a failed scan doesn't block every later one.
                if (_run == run)
                    _run = null;
                run.Cancel.Dispose();
            }
            if (background)
                NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.THREAD_MODE_BACKGROUND_END);
            GiveMemoryBack();
            Changed?.Invoke();
        }
        if (again)
            StartScan(run.Urgent);
    }

    /// <summary>The drives turned on, other drives first: they hold the programs Windows lost track of after a reinstall.</summary>
    private List<DriveChoice> DrivesToScan() =>
        Drives()
            .Where(d => d.Scan)
            .OrderBy(d => d.System)
            .ThenBy(d => d.Drive, StringComparer.Ordinal)
            .ToList();

    private void CheckMemory()
    {
        uint load = MemoryGuard.LoadPercent;
        bool holding;
        lock (_gate)
            holding = _run != null || _ready != null;
        if (holding && load >= MemoryGuard.LowPercent)
            Drop();
        else if (!holding && KeepReady && load < MemoryGuard.TightPercent)
            StartScan(urgent: false);
    }

    /// <summary>Forgets the list and stops any scan. The next program search scans again.</summary>
    public void Drop()
    {
        lock (_gate)
        {
            if (_run == null && _ready == null)
                return;
            _run?.Cancel.Cancel();
            _run = null;
            _ready = null;
        }
        GiveMemoryBack();
        Changed?.Invoke();
    }

    /// <summary>Hands the memory the scan used back to Windows instead of keeping it reserved.</summary>
    private static void GiveMemoryBack()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);
    }

    public void Dispose()
    {
        _memoryCheck.Dispose();
        lock (_gate)
            _run?.Cancel.Cancel();
    }
}

/// <summary>How full the PC's memory is, as Windows reports it (0–100%).</summary>
internal static class MemoryGuard
{
    /// <summary>Above this, background scans wait.</summary>
    public const uint TightPercent = 80;

    /// <summary>Above this, the program list is dropped.</summary>
    public const uint LowPercent = 90;

    public static uint LoadPercent
    {
        get
        {
            var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            return NativeMethods.GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
        }
    }
}
