using System;
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

    public TrayIcon(Action onSearch, Action onChangeHotkey, Action<bool> onStartWithWindowsChanged, Action onExit)
    {
        _searchItem = new ToolStripMenuItem("Search screen", null, (_, _) => onSearch());
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startWithWindowsItem.Click += (_, _) => onStartWithWindowsChanged(_startWithWindowsItem.Checked);

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_searchItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Change shortcut…", null, (_, _) => onChangeHotkey());
        _menu.Items.Add(_startWithWindowsItem);
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
