using System.Windows.Input;
using Xunit;

namespace SearchAnything.Tests;

public class HotkeyTests
{
    [Fact]
    public void Default_is_ctrl_alt_f()
    {
        Assert.Equal("Ctrl+Alt+F", Hotkey.Default.ToString());
    }

    [Theory]
    [InlineData("Ctrl+Alt+F", ModifierKeys.Control | ModifierKeys.Alt, Key.F)]
    [InlineData("ctrl + shift + 1", ModifierKeys.Control | ModifierKeys.Shift, Key.D1)]
    [InlineData("Win+Space", ModifierKeys.Windows, Key.Space)]
    public void Parses_text(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("Ctrl+Alt+F")]
    [InlineData("Ctrl+Shift+1")]
    [InlineData("Alt+Win+F12")]
    public void Round_trips_through_text(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var again));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F")]              // no modifier would block normal typing
    [InlineData("Shift+F")]        // Shift alone isn't enough either
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+F+G")]       // two keys
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+12")]
    public void Rejects_invalid_text(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }
}
