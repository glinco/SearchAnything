using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;

namespace SearchAnything;

/// <summary>What a program search found, and whether more may still arrive.</summary>
internal sealed record ProgramResults(IReadOnlyList<ProgramEntry> Top, int Total, int Known, string? ScanningDrive);

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
    }

    private readonly object _gate = new();
    private readonly Timer _memoryCheck;
    private List<ProgramEntry>? _ready;
    private DateTime _readyAt;
    private ScanRun? _run;

    /// <summary>Raised from a background thread whenever results change.</summary>
    public event Action? Changed;

    public bool KeepReady { get; private set; }

    public ProgramCatalog(bool keepReady)
    {
        KeepReady = keepReady;
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

    public ProgramResults Find(string query, int limit)
    {
        lock (_gate)
        {
            // While refreshing, keep showing the previous list; on the first scan, show what's found so far.
            var source = _ready ?? _run?.Found ?? new List<ProgramEntry>();
            var top = ProgramMatcher.Find(source, query, limit, out int total);
            return new ProgramResults(top, total, source.Count, _run?.Drive);
        }
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
        try
        {
            foreach (var drive in FixedDrives())
            {
                run.Drive = drive.TrimEnd('\\');
                ProgramScanner.Scan(drive, found: entry =>
                {
                    lock (_gate)
                    {
                        if (run.Found.Count < MaxPrograms)
                            run.Found.Add(entry);
                    }
                }, eachFolder: () =>
                {
                    cancel.ThrowIfCancellationRequested();
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
            lock (_gate)
            {
                if (_run == run)
                {
                    _ready = run.Found;
                    _readyAt = DateTime.UtcNow;
                    _run = null;
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
    }

    /// <summary>Other drives first: they hold the programs Windows lost track of after a reinstall.</summary>
    private static IEnumerable<string> FixedDrives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .OrderBy(root => string.Equals(root, system, StringComparison.OrdinalIgnoreCase))
            .ThenBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
