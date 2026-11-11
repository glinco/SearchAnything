using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SearchAnything;

/// <summary>Notification-area icon: left click searches, right click shows the menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _searchItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly ToolStripMenuItem _keepProgramsItem;
    private readonly ToolStripMenuItem _drivesItem;

    public TrayIcon(Action onSearch, Action onChangeHotkey, Action<bool> onStartWithWindowsChanged,
        Action<bool> onKeepProgramsChanged, Func<IReadOnlyList<DriveChoice>> listDrives,
        Action<string, bool> onDriveChanged, Action onExit)
    {
        _searchItem = new ToolStripMenuItem("Search screen", null, (_, _) => onSearch());
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startWithWindowsItem.Click += (_, _) => onStartWithWindowsChanged(_startWithWindowsItem.Checked);
        _keepProgramsItem = new ToolStripMenuItem("Keep program list ready")
        {
            CheckOnClick = true,
            ToolTipText = "Faster program search, using a little memory. Off: programs are scanned each time you search them.",
        };
        _keepProgramsItem.Click += (_, _) => onKeepProgramsChanged(_keepProgramsItem.Checked);

        _drivesItem = new ToolStripMenuItem("Drives to scan for programs")
        {
            ToolTipText = "Turn a drive off to skip it. USB sticks and memory cards are off until you turn them on.",
        };
        // Filled each time it opens, so drives plugged in since are listed.
        _drivesItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        _drivesItem.DropDownOpening += (_, _) => FillDrives(listDrives(), onDriveChanged);
        // Stay open while ticking drives on and off.
        _drivesItem.DropDown.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                e.Cancel = true;
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_searchItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Change shortcut…", null, (_, _) => onChangeHotkey());
        _menu.Items.Add(_startWithWindowsItem);
        _menu.Items.Add(_keepProgramsItem);
        _menu.Items.Add(_drivesItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => onExit());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "SearchAnything",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                onSearch();
        };
    }

    public void SetHotkeyText(string hotkey)
    {
        _searchItem.ShortcutKeyDisplayString = hotkey;
        _icon.Text = $"SearchAnything ({hotkey})";
    }

    public void SetStartWithWindows(bool enabled) => _startWithWindowsItem.Checked = enabled;

    public void SetKeepPrograms(bool enabled) => _keepProgramsItem.Checked = enabled;

    private void FillDrives(IReadOnlyList<DriveChoice> drives, Action<string, bool> onDriveChanged)
    {
        _drivesItem.DropDownItems.Clear();
        if (drives.Count == 0)
            _drivesItem.DropDownItems.Add(new ToolStripMenuItem("No drives found") { Enabled = false });
        foreach (var drive in drives)
        {
            var item = new ToolStripMenuItem(DriveText(drive)) { Checked = drive.Scan, CheckOnClick = true };
            item.Click += (_, _) => onDriveChanged(drive.Drive, item.Checked);
            _drivesItem.DropDownItems.Add(item);
        }
    }

    /// <summary>"D:  Games", "C:  (Windows)", "F:  KINGSTON  (removable)".</summary>
    internal static string DriveText(DriveChoice drive)
    {
        var text = drive.Label.Length > 0 ? $"{drive.Drive}  {drive.Label}" : drive.Drive;
        if (drive.System)
            text += "  (Windows)";
        if (drive.Removable)
            text += "  (removable)";
        // A lone & would underline the next letter in a menu.
        return text.Replace("&", "&&");
    }

    public void Notify(string message) =>
        _icon.ShowBalloonTip(4000, "SearchAnything", message, ToolTipIcon.Info);

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.FromArgb(255, 255, 190, 0), 4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawEllipse(pen, 4, 4, 17, 17);
            g.DrawLine(pen, 19, 19, 28, 28);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
