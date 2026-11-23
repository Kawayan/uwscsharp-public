using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeClickOutcomeTests
{
    private sealed class Platform : OwnedListSelectionPlatform
    {
        internal int Commands;

        public override void Post(nint target, uint message, nint parameter, nint data)
        {
            if (message == 0x111)
            {
                Commands++;
            }
        }

        public override void Sleep(int milliseconds, CancellationToken token) =>
            token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSelectionRetainsMatchBeforeBackendRejects(bool combo)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX",
            "", combo ? 3 : 1, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "first", 2, 1000, out _);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "target", 2, 1000, out _);
        fixture.Command = (target, command) =>
        {
            if (target == control && ((command >> 16) & 0xffff) == 1)
            {
                Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 0, 0);
            }
        };

        var platform = new Platform();
        int matches = 0;
        Assert.False(NativeListControl.Select(control, "missing", default,
            fixture.Root, true, platform, onSingleMatch: () => matches++));
        Assert.Equal(0, matches);
        Assert.False(NativeListControl.Select(control, "target", default,
            fixture.Root, true, platform, onSingleMatch: () => matches++));
        Assert.Equal(1, matches);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("missing", 0u, 701u, false, false)]
    [InlineData("target", 3u, 701u, true, false)]
    [InlineData("target", 0u, 0u, true, false)]
    [InlineData("target", 0u, 701u, true, true)]
    public void MenuSeparatesNoMatchFromRefusedOrSuccessfulAction(
        string name, uint state, uint command, bool expectedMatch, bool expectedSuccess)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint menu = MenuControlTests.CreateMenu();
        try
        {
            Assert.True(MenuControlTests.AppendMenuW(menu, state, command, "target"));
            Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
            bool matched = false;
            Assert.Equal(expectedSuccess, NativeMenuControl.Invoke(fixture.Root,
                name, 1, default, true, new Platform(), true,
                onMatch: () => matched = true));
            Assert.Equal(expectedMatch, matched);
        }
        finally
        {
            MenuControlTests.SetMenu(fixture.Root, 0);
            MenuControlTests.DestroyMenu(menu);
        }
    }
}
