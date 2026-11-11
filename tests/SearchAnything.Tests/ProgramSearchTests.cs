using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SearchAnything.Tests;

public class ProgramSearchTests
{
    private static ProgramEntry Program(string name, string path) => new(name, path);

    private static List<string> Find(string query, params ProgramEntry[] programs) =>
        ProgramMatcher.Find(programs, query, 10, out _).Select(p => p.Name).ToList();

    [Theory]
    [InlineData("unins000.exe")]
    [InlineData("Uninstall.exe")]
    [InlineData("setup.exe")]
    [InlineData("UpdateManager.exe")]
    [InlineData("Discord_updater.exe")]
    [InlineData("crashpad_handler.exe")]
    [InlineData("notification_helper.exe")]
    [InlineData("VC_redist.x64.exe")]
    [InlineData("readme.txt")]
    public void Skips_files_that_are_not_programs(string file) =>
        Assert.False(ProgramFilter.IsProgramFile(file));

    [Theory]
    [InlineData("blender.exe")]
    [InlineData("Steam.exe")]
    [InlineData("notepad++.exe")]
    [InlineData("hollow_knight.EXE")]
    public void Keeps_real_programs(string file) =>
        Assert.True(ProgramFilter.IsProgramFile(file));

    [Theory]
    [InlineData("node_modules")]
    [InlineData("$Recycle.Bin")]
    [InlineData(".git")]
    [InlineData("_CommonRedist")]
    [InlineData("Package Cache")]
    public void Skips_cache_and_system_folders_anywhere(string folder) =>
        Assert.False(ProgramFilter.ShouldEnterFolder(folder, atDriveRoot: false));

    [Fact]
    public void Skips_windows_folder_only_at_the_drive_root()
    {
        Assert.False(ProgramFilter.ShouldEnterFolder("Windows", atDriveRoot: true));
        Assert.True(ProgramFilter.ShouldEnterFolder("Windows", atDriveRoot: false));
        Assert.True(ProgramFilter.ShouldEnterFolder("Games", atDriveRoot: true));
    }

    [Fact]
    public void Names_come_from_version_info_without_trademark_signs()
    {
        Assert.Equal("Photoshop", ProgramEntry.CleanName("Photoshop®", null, @"D:\Adobe\Photoshop.exe"));
        Assert.Equal("Some Tool", ProgramEntry.CleanName("Some Tool(TM)", null, @"D:\x\tool.exe"));
        Assert.Equal("Blender", ProgramEntry.CleanName("  ", "Blender", @"E:\Blender\blender.exe"));
        Assert.Equal("blender", ProgramEntry.CleanName(null, null, @"E:\Blender\blender.exe"));
    }

    [Fact]
    public void Prefers_the_description_unless_it_is_just_a_file_name()
    {
        Assert.Equal("Microsoft Word", ProgramEntry.CleanName("Microsoft Office", "Microsoft Word", @"C:\Office16\WINWORD.EXE"));
        Assert.Equal("Steam", ProgramEntry.CleanName("Steam", "gameoverlayui.exe", @"C:\Steam\gameoverlayui64.exe"));
    }

    [Fact]
    public void Finds_suite_programs_by_their_product_too()
    {
        var word = new ProgramEntry("Microsoft Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", "Microsoft Office");

        Assert.Single(Find("word", word));
        Assert.Single(Find("office", word));
    }

    [Fact]
    public void Shows_one_row_per_program_and_install_picking_the_main_exe()
    {
        var top = ProgramMatcher.Find(new[]
        {
            Program("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe"),
            Program("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            Program("Google Chrome", @"C:\Program Files\Google\Chrome\Application\156.0\chrome_pwa_launcher.exe"),
            Program("Google Chrome", @"D:\Portable\Chrome\chrome.exe"),
        }, "chrome", 10, out int total);

        Assert.Equal(2, total);
        Assert.Contains(top, p => p.Path == @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        Assert.Contains(top, p => p.Path == @"D:\Portable\Chrome\chrome.exe");
    }

    [Fact]
    public void Ranks_name_starts_before_name_contains()
    {
        var names = Find("code",
            Program("Barcode Studio", @"D:\Barcode\barcode.exe"),
            Program("Code Writer", @"D:\CodeWriter\cw.exe"),
            Program("Visual Studio Code", @"D:\VSCode\Code.exe"));

        Assert.Equal(new[] { "Code Writer", "Visual Studio Code", "Barcode Studio" }, names);
    }

    [Fact]
    public void Ranks_real_apps_above_tools_with_a_similar_name()
    {
        var names = Find("gimp",
            new ProgramEntry("gimptool", @"C:\Program Files\GIMP 3\bin\gimptool.exe", size: 40_000, described: false),
            new ProgramEntry("GNU Image Manipulation Program", @"C:\Program Files\GIMP 3\bin\gimp-3.exe", "GNU Image Manipulation Program", 9_000_000));
        Assert.Equal("GNU Image Manipulation Program", names[0]);

        names = Find("word",
            new ProgramEntry("Word Converter", @"C:\Office16\Wordconv.exe", size: 40_000),
            new ProgramEntry("Microsoft Word", @"C:\Office16\WINWORD.EXE", size: 2_000_000));
        Assert.Equal("Microsoft Word", names[0]);
    }

    [Theory]
    [InlineData("Git Setup", true)]
    [InlineData("Notepad++ Installer", true)]
    [InlineData("Standalone Updater", true)]
    [InlineData("Microsoft Word", false)]
    [InlineData("Setupapi tool", false)]
    [InlineData(null, false)]
    public void Recognizes_installers_by_description(string? description, bool installer) =>
        Assert.Equal(installer, ProgramFilter.IsInstallerDescription(description));

    [Fact]
    public void Ignores_case_and_accents()
    {
        Assert.Single(Find("musica", Program("Música Pro", @"D:\Musica\musica.exe")));
        Assert.Single(Find("MÚSICA", Program("Musica Pro", @"D:\Musica\musica.exe")));
    }

    [Fact]
    public void Finds_by_file_or_folder_name_when_the_product_name_differs()
    {
        var game = Program("Unity Player", @"E:\Games\Hollow Knight\hollow_knight.exe");

        Assert.Single(Find("hollow", game));
        Assert.Single(Find("hollow_knight", game));
    }

    [Fact]
    public void Every_word_of_a_multi_word_query_must_appear()
    {
        var code = Program("Visual Studio Code", @"D:\VSCode\Code.exe");

        Assert.Single(Find("visual code", code));
        Assert.Empty(Find("visual paint", code));
    }

    [Fact]
    public void Reports_the_total_beyond_the_limit()
    {
        var programs = Enumerable.Range(0, 30).Select(i => Program($"Tool {i}", $@"D:\Tools\tool{i}.exe"));

        var top = ProgramMatcher.Find(programs, "tool", 8, out int total);

        Assert.Equal(8, top.Count);
        Assert.Equal(30, total);
    }

    [Theory]
    [InlineData(@"e:\Games\x.exe", "E:")]
    [InlineData(@"D:\", "D:")]
    [InlineData(@"\\server\share\x.exe", "")]
    public void Knows_the_drive_of_a_program(string path, string drive) =>
        Assert.Equal(drive, ProgramEntry.DriveOf(path));

    [Fact]
    public void Counts_matches_per_drive_and_narrows_to_one()
    {
        var programs = new[]
        {
            Program("Blender", @"C:\Program Files\Blender\blender.exe"),
            Program("Blender", @"D:\Apps\Blender\blender.exe"),
            Program("Blender Launcher", @"D:\Tools\BlenderLauncher\launcher.exe"),
            Program("Steam", @"E:\Steam\steam.exe"),
        };

        var all = ProgramMatcher.Find(programs, "blender", 10, drive: null, out int total, out var drives);
        Assert.Equal(3, total);
        Assert.Equal(3, all.Count);
        Assert.Equal(new[] { new DriveCount("C:", 1), new DriveCount("D:", 2) }, drives);

        // Narrowing keeps the counts for every drive, so the other chips still show theirs.
        var onD = ProgramMatcher.Find(programs, "blender", 10, drive: "d:", out total, out drives);
        Assert.Equal(2, total);
        Assert.All(onD, p => Assert.Equal("D:", p.Drive));
        Assert.Equal(2, drives.Count);
    }

    [Fact]
    public void Remembers_drives_turned_off_and_scans_internal_drives_by_default()
    {
        var system = ProgramEntry.DriveOf(Environment.SystemDirectory);

        using (var programs = new ProgramCatalog(keepReady: false))
            Assert.True(programs.Drives().Single(d => d.Drive == system).Scan);

        using (var programs = new ProgramCatalog(keepReady: false, new Dictionary<string, bool> { [system] = false }))
        {
            var drive = programs.Drives().Single(d => d.Drive == system);
            Assert.False(drive.Scan);
            Assert.True(drive.System);
            Assert.Equal(false, programs.DriveChoices()[system]);
        }
    }

    [Theory]
    [InlineData("D:", "Games", false, false, "D:  Games")]
    [InlineData("C:", "", false, true, "C:  (Windows)")]
    [InlineData("F:", "Tom & Jerry", true, false, "F:  Tom && Jerry  (removable)")]
    public void Names_drives_in_the_tray_menu(string drive, string label, bool removable, bool system, string expected) =>
        Assert.Equal(expected, TrayIcon.DriveText(new DriveChoice(drive, label, removable, system, Scan: true)));

    [Fact]
    public void Scanner_keeps_programs_and_skips_junk()
    {
        var root = Path.Combine(Path.GetTempPath(), "SearchAnythingTests", Guid.NewGuid().ToString("N"));
        try
        {
            Touch(root, @"Apps\Blender\blender.exe");
            Touch(root, @"Apps\Blender\unins000.exe");
            Touch(root, @"Apps\Blender\readme.txt");
            Touch(root, @"Games\Hollow Knight\hollow_knight.exe");
            Touch(root, @"Games\Hollow Knight\_CommonRedist\dxsetup.exe");
            Touch(root, @"Code\project\node_modules\esbuild\esbuild.exe");

            var found = new List<string>();
            int folders = 0;
            ProgramScanner.Scan(root, p => found.Add(Path.GetFileName(p.Path)), () => folders++);

            Assert.Equal(new[] { "blender.exe", "hollow_knight.exe" }, found.OrderBy(f => f));
            Assert.True(folders > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Blender", "Blender.lnk")]
    [InlineData("AC/DC: Live?", "AC DC  Live.lnk")]
    [InlineData("...", "Program.lnk")]
    public void Shortcut_names_are_safe_file_names(string name, string expected) =>
        Assert.Equal(expected, StartMenu.ShortcutFileName(name));

    [Fact]
    public void Writes_a_shortcut_once_and_leaves_an_existing_one_alone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SearchAnythingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var notepad = Program("My Notepad", Path.Combine(Environment.SystemDirectory, "notepad.exe"));

            Assert.True(StartMenu.Add(notepad, folder));
            Assert.False(StartMenu.Add(notepad, folder));

            // Read it back the way Windows does, to be sure it points at the program.
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.CreateShortcut(Path.Combine(folder, "My Notepad.lnk"));
            Assert.Equal(notepad.Path, (string)link.TargetPath, ignoreCase: true);
            Assert.Equal(notepad.Folder, (string)link.WorkingDirectory, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Touch(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a real program");
    }
}
