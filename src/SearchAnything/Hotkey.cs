using System;
using System.Linq;
using System.Text;
using System.Windows.Input;

namespace SearchAnything;

/// <summary>A keyboard shortcut such as Ctrl+Alt+F, stored in settings as that text.</summary>
internal readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static Hotkey Default => new(ModifierKeys.Control | ModifierKeys.Alt, Key.F);

    /// <summary>Requires Ctrl, Alt or Win, so the shortcut can't swallow normal typing.</summary>
    public bool IsValid =>
        Key != Key.None &&
        (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? NativeMethods.MOD_CONTROL : 0) |
        (Modifiers.HasFlag(ModifierKeys.Alt) ? NativeMethods.MOD_ALT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Shift) ? NativeMethods.MOD_SHIFT : 0) |
        (Modifiers.HasFlag(ModifierKeys.Windows) ? NativeMethods.MOD_WIN : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString() => FormatModifiers(Modifiers) + KeyName(Key);

    public static string FormatModifiers(ModifierKeys modifiers)
    {
        var text = new StringBuilder();
        if (modifiers.HasFlag(ModifierKeys.Control)) text.Append("Ctrl+");
        if (modifiers.HasFlag(ModifierKeys.Alt)) text.Append("Alt+");
        if (modifiers.HasFlag(ModifierKeys.Shift)) text.Append("Shift+");
        if (modifiers.HasFlag(ModifierKeys.Windows)) text.Append("Win+");
        return text.ToString();
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = ModifierKeys.None;
        var key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (key != Key.None || !TryParseKey(part, out key))
                        return false;
                    break;
            }
        }
        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }

    private static string KeyName(Key key) =>
        key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString();

    private static bool TryParseKey(string text, out Key key)
    {
        key = Key.None;
        if (text.All(char.IsDigit))
        {
            if (text.Length != 1)
                return false;
            key = Key.D0 + (text[0] - '0');
            return true;
        }
        return Enum.TryParse(text, ignoreCase: true, out key) && key != Key.None;
    }
}
