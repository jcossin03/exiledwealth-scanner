using EwCapture.Core;

namespace EwCapture.Tests;

public class ReadKeyTests
{
    [Fact]
    public void TheDefaultIsCtrlShiftF12AndRaisesNothing()
    {
        Assert.Equal("Ctrl+Shift+F12", ReadKey.Default.ToString());
        Assert.Null(ReadKey.Default.Blocker());
        Assert.Null(ReadKey.Default.Warning());
    }

    [Theory]
    [InlineData("CS:123", "Ctrl+Shift+F12")]
    [InlineData("A:75", "Alt+K")]
    [InlineData(":118", "F7")]
    [InlineData("CAS:101", "Ctrl+Alt+Shift+Num 5")]
    public void StoresAndReadsBack(string stored, string shown)
    {
        var key = ReadKey.Parse(stored);
        Assert.NotNull(key);
        Assert.Equal(shown, key.Value.ToString());
        Assert.Equal(stored, key.Value.Store());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CS")]
    [InlineData("CS:")]
    [InlineData("XS:123")]
    [InlineData("CS:0")]
    [InlineData("CS:999")]
    [InlineData("CS:12:3")]
    public void ADamagedFileIsNotAKey(string? stored) => Assert.Null(ReadKey.Parse(stored));

    [Fact]
    public void OnlyAModifierOnItsOwnIsRefused()
    {
        Assert.NotNull(new ReadKey(true, false, false, 0x11).Blocker());  // Ctrl
        Assert.NotNull(new ReadKey(false, false, true, 0xA0).Blocker());  // left Shift
        // Players rebind the game's keys, so its defaults only warn.
        Assert.Null(new ReadKey(true, false, false, 'C').Blocker());
        Assert.Null(new ReadKey(false, false, false, 'Q').Blocker());
    }

    [Theory]
    [InlineData(true, false, false, 'C')]   // copy an item
    [InlineData(true, false, false, 'F')]   // stash search
    [InlineData(true, true, false, 'C')]    // advanced copy
    [InlineData(false, true, false, 0x73)]  // Alt+F4
    [InlineData(false, false, false, 'Q')]  // a plain key the game or chat needs
    [InlineData(false, false, true, 'Q')]   // Shift+Q types a capital
    public void WarnsAboutKeysTheGameOrWindowsUseByDefault(bool ctrl, bool alt, bool shift, int vk) =>
        Assert.NotNull(new ReadKey(ctrl, alt, shift, vk).Warning());

    [Theory]
    [InlineData(false, false, false, 0x76)] // F7
    [InlineData(false, false, true, 0x7B)]  // Shift+F12
    [InlineData(true, true, false, 'K')]
    [InlineData(true, false, true, 'C')]    // Ctrl+Shift+C is not the copy key
    public void LeavesOtherKeysAlone(bool ctrl, bool alt, bool shift, int vk) =>
        Assert.Null(new ReadKey(ctrl, alt, shift, vk).Warning());
}
