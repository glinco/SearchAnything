using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text.RegularExpressions;

namespace SearchAnything;

/// <summary>One program found on disk: the name people know it by, and where its .exe is.</summary>
internal sealed class ProgramEntry
{
    public string Name { get; }
    public string Path { get; }
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>The first three path parts ("E:\Games\Hollow Knight"): one install, for grouping its .exe files.</summary>
    internal string Install { get; }

    /// <summary>"E:", for the drive chips and the drives the user chose to scan.</summary>
    internal string Drive { get; }

    // Folded once here, so a keystroke doesn't refold thousands of names.
    internal string FoldedName { get; }
    internal string FoldedProduct { get; }
    internal string FoldedFile { get; }
    internal string FoldedFolder { get; }

    /// <summary>Bytes on disk. Main programs are big; the helpers around them are small.</summary>
    internal long Size { get; }

    /// <summary>Named by its version info. Unnamed .exe files are mostly tools, so they rank lower.</summary>
    internal bool Described { get; }

    /// <param name="product">The suite or product it belongs to ("Microsoft Office" for Word), also searchable.</param>
    public ProgramEntry(string name, string path, string? product = null, long size = 0, bool described = true)
    {
        Name = name;
        Path = path;
        Size = size;
        Described = described;
        Install = string.Join('\\', path.Split('\\').Take(3));
        Drive = DriveOf(path);
        FoldedName = SearchIndex.Fold(name);
        FoldedProduct = SearchIndex.Fold(product ?? "");
        FoldedFile = SearchIndex.Fold(System.IO.Path.GetFileNameWithoutExtension(path));
        FoldedFolder = SearchIndex.Fold(LastFolders(path));
    }

    /// <summary>"e:\Games\x.exe" or "E:\" → "E:"; empty for paths without a drive letter.</summary>
    internal static string DriveOf(string path) =>
        path.Length >= 2 && path[1] == ':' ? path[..2].ToUpperInvariant() : "";

    /// <summary>
    /// Names the program from its version info ("Microsoft Word"), not its file name ("WINWORD").
    /// Null for installers that only reveal themselves there ("Git-2.48.1-64-bit.exe" is "Git Setup").
    /// </summary>
    public static ProgramEntry? FromFile(string path, long size)
    {
        string? product = null, description = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            product = info.ProductName;
            description = info.FileDescription;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable version info: fall back to the file name.
        }
        if (ProgramFilter.IsInstallerDescription(description))
            return null;
        bool described = Clean(product).Length > 0 || Clean(description).Length > 0;
        return new ProgramEntry(CleanName(product, description, path), path, Clean(product), size, described);
    }

    /// <summary>
    /// The description names the program itself ("Microsoft Word") where the product names the suite
    /// ("Microsoft Office"), so it wins unless it's just a file name ("gameoverlayui.exe").
    /// </summary>
    internal static string CleanName(string? product, string? description, string path)
    {
        var file = System.IO.Path.GetFileNameWithoutExtension(path);
        var described = Clean(description);
        if (described.Length > 0 && !described.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(described, file, StringComparison.OrdinalIgnoreCase))
            return described;
        var named = Clean(product);
        return named.Length > 0 ? named : described.Length > 0 ? described : file;
    }

    private static string Clean(string? text)
    {
        var name = Regex.Replace(text ?? "", @"®|™|\((R|TM)\)", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    /// <summary>How likely this .exe is the one to launch: "chrome.exe" for Google Chrome, not "chrome_proxy.exe".</summary>
    internal int LaunchRank()
    {
        var squashed = FoldedName.Replace(" ", "");
        bool fileIsTheName = FoldedFile.Length > 0 && (squashed.Contains(FoldedFile) || FoldedFile.Contains(squashed));
        return (fileIsTheName ? 0 : 1000) + Path.Count(c => c == '\\') * 50 + FoldedFile.Length;
    }

    /// <summary>"E:\Games\Hollow Knight\hollow_knight.exe" → "Games Hollow Knight": enough to find it by its folder.</summary>
    private static string LastFolders(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        var parent = System.IO.Path.GetDirectoryName(folder ?? "");
        return $"{System.IO.Path.GetFileName(parent)} {System.IO.Path.GetFileName(folder)}".Trim();
    }
}

/// <summary>Decides which folders are worth walking into and which .exe files are real programs.</summary>
internal static class ProgramFilter
{
    // Only skipped right under a drive root: a "Windows" folder inside a game is fine.
    private static readonly HashSet<string> RootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Recovery", "PerfLogs", "System Volume Information", "Config.Msi",
    };

    // Skipped anywhere: system stores, caches, redistributables and developer tool folders full of small .exe files.
    private static readonly HashSet<string> AnyFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinSxS", "WindowsApps", "Temp", "tmp", "Package Cache", "Installer", "node_modules", "site-packages",
        "obj", "usr", "mingw32", "mingw64", "_CommonRedist", "CommonRedist", "Redist", "Redistributables", "DirectX",
    };

    private static readonly string[] SkipPrefixes =
    {
        "unins", "setup", "install", "update", "vc_redist", "vcredist", "dxsetup", "dotnet-", "crash", "bugreport",
        "squirrel", "elevat",
    };

    private static readonly string[] SkipContains =
    {
        "uninstall", "crashpad", "crashreport", "crashhandler", "errorreport", "helper", "updater", "_setup", "-setup",
        "redist", "installer",
    };

    public static bool ShouldEnterFolder(string name, bool atDriveRoot) =>
        !name.StartsWith('$') && !name.StartsWith('.') &&
        !AnyFolders.Contains(name) &&
        !(atDriveRoot && RootFolders.Contains(name));

    public static bool IsProgramFile(string fileName)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var name = fileName[..^4].ToLowerInvariant();
        return !SkipPrefixes.Any(name.StartsWith) && !SkipContains.Any(name.Contains);
    }

    public static bool IsInstallerDescription(string? description) =>
        description != null &&
        Regex.IsMatch(description, @"\b(setup|installer|uninstall|uninstaller|updater)\b", RegexOptions.IgnoreCase);
}

/// <summary>Walks a folder tree one entry at a time, keeping only programs, so the scan itself holds almost nothing.</summary>
internal static class ProgramScanner
{
    /// <param name="eachFolder">Called before entering every folder: the place to cancel, pause or report progress.</param>
    public static void Scan(string root, Action<ProgramEntry> found, Action eachFolder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Junctions and symlinks can loop back on themselves or into folders already scanned.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        int rootLength = System.IO.Path.TrimEndingDirectorySeparator(root).Length;
        var files = new FileSystemEnumerable<(string Path, long Size)>(root, (ref FileSystemEntry e) => (e.ToFullPath(), e.Length), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && ProgramFilter.IsProgramFile(e.FileName.ToString()),
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                eachFolder();
                bool atRoot = System.IO.Path.TrimEndingDirectorySeparator(e.Directory).Length == rootLength;
                return ProgramFilter.ShouldEnterFolder(e.FileName.ToString(), atRoot);
            },
        };
        foreach (var (path, size) in files)
        {
            if (ProgramEntry.FromFile(path, size) is { } program)
                found(program);
        }
    }
}

/// <summary>How many programs matched on one drive.</summary>
internal sealed record DriveCount(string Drive, int Count);

/// <summary>Accent- and case-insensitive program search, best matches first.</summary>
internal static class ProgramMatcher
{
    public static List<ProgramEntry> Find(IEnumerable<ProgramEntry> programs, string query, int limit, out int total) =>
        Find(programs, query, limit, drive: null, out total, out _);

    /// <param name="drive">Only programs on this drive ("D:"), or null for all of them.</param>
    /// <param name="drives">Matches on each drive, counted before <paramref name="drive"/> narrows them.</param>
    public static List<ProgramEntry> Find(IEnumerable<ProgramEntry> programs, string query, int limit, string? drive,
        out int total, out List<DriveCount> drives)
    {
        var words = SearchIndex.Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var folded = string.Join(' ', words);
        var hits = new List<(ProgramEntry Program, int Score)>();
        foreach (var program in programs)
        {
            int score = Score(program, folded, words);
            if (score >= 0)
                hits.Add((program, score));
        }
        // One row per program per install: Steam's helpers fold into steam.exe.
        var best = hits
            .GroupBy(h => (h.Program.FoldedName, h.Program.Install.ToLowerInvariant()))
            .Select(g => g.OrderBy(h => h.Score).ThenBy(h => h.Program.LaunchRank()).First())
            .ToList();
        drives = best
            .GroupBy(h => h.Program.Drive)
            .Select(g => new DriveCount(g.Key, g.Count()))
            .OrderBy(d => d.Drive, StringComparer.Ordinal)
            .ToList();
        if (drive != null)
            best = best.Where(h => string.Equals(h.Program.Drive, drive, StringComparison.OrdinalIgnoreCase)).ToList();
        total = best.Count;
        // Within a tier, real apps before tools: named by version info, then bigger first ("Microsoft Word"
        // before "Word Converter", GIMP before gimptool).
        return best
            .OrderBy(h => h.Score)
            .ThenByDescending(h => h.Program.Described)
            .ThenByDescending(h => h.Program.Size)
            .ThenBy(h => h.Program.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(h => h.Program)
            .ToList();
    }

    /// <summary>Lower is better; -1 means no match.</summary>
    private static int Score(ProgramEntry p, string query, string[] words)
    {
        if (query.Length == 0)
            return 0;
        // A name taken from the file name is only as good as a file name match.
        if (p.Described)
        {
            if (p.FoldedName == query)
                return 0;
            int at = p.FoldedName.IndexOf(query, StringComparison.Ordinal);
            // Start of any word: "word" finds "Microsoft Word" as well as "Word Converter".
            if (at == 0 || at > 0 && !char.IsLetterOrDigit(p.FoldedName[at - 1]))
                return 1;
            if (at > 0)
                return 2;
        }
        if (p.FoldedFile.StartsWith(query, StringComparison.Ordinal) ||
            p.FoldedProduct.Contains(query, StringComparison.Ordinal) ||
            p.FoldedFolder.Contains(query, StringComparison.Ordinal))
            return 3;
        if (p.FoldedFile.Contains(query, StringComparison.Ordinal) || p.FoldedName.Contains(query, StringComparison.Ordinal))
            return 4;
        // "visual code" finds "Visual Studio Code": every word somewhere.
        if (words.Length > 1 && words.All(w => p.FoldedName.Contains(w, StringComparison.Ordinal) ||
                                               p.FoldedProduct.Contains(w, StringComparison.Ordinal) ||
                                               p.FoldedFile.Contains(w, StringComparison.Ordinal) ||
                                               p.FoldedFolder.Contains(w, StringComparison.Ordinal)))
            return 5;
        return -1;
    }
}
