using System.Windows;
using System.Windows.Input;

namespace SearchAnything;

/// <summary>Records the next key combination the user presses.</summary>
public partial class HotkeyDialog : Window
{
    internal Hotkey? Result { get; private set; }

    internal HotkeyDialog(Hotkey current)
    {
        InitializeComponent();
        Display.Text = current.ToString();
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => Activate();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Swallow every key so the combination is recorded instead of moving focus or pressing buttons.
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            return;
        }
        if (IsModifier(key))
        {
            Display.Text = Hotkey.FormatModifiers(modifiers) + "…";
            return;
        }

        var candidate = new Hotkey(modifiers, key);
        Display.Text = candidate.ToString();
        Result = candidate.IsValid ? candidate : null;
        SaveButton.IsEnabled = candidate.IsValid;
        Error.Text = candidate.IsValid ? "" : "Add Ctrl, Alt or Win so normal typing isn't blocked.";
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
