using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SearchAnything;

/// <summary>
/// Adds a program to the current user's Start menu, so Windows Search finds it again on its own.
/// </summary>
internal static class StartMenu
{
    public static string ProgramsFolder => Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    /// <param name="folder">Where to put the shortcut; the Start menu unless a test says otherwise.</param>
    /// <returns>False if a shortcut with that name is already there (it is left alone).</returns>
    public static bool Add(ProgramEntry program, string? folder = null)
    {
        var target = Path.Combine(folder ?? ProgramsFolder, ShortcutFileName(program.Name));
        if (File.Exists(target))
            return false;

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(program.Path);
            link.SetWorkingDirectory(program.Folder);
            link.SetDescription(program.Name);
            ((IPersistFile)link).Save(target, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
        return true;
    }

    internal static string ShortcutFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim(' ', '.');
        if (safe.Length == 0)
            safe = "Program";
        if (safe.Length > 100)
            safe = safe[..100].TrimEnd();
        return safe + ".lnk";
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
