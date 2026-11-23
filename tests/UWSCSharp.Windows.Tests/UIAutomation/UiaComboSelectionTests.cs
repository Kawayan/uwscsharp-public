using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class UiaSelectionTests
{
    [Theory]
    [InlineData("日本語", false)]
    [InlineData("", false)]
    [InlineData(" raw ", false)]
    [InlineData("日本語", true)]
    [InlineData("", true)]
    [InlineData(" raw ", true)]
    public async Task ComboSelectionReadDoesNotOpenOrCloseItsPopup(
        string expected, bool closeOnExpand)
    {
        nint combo = 0;
        int expansions = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            combo = host.Child("COMBOBOX", "", 3, 10, height: 100);
            Assert.NotEqual(0, Native.SendText(combo, 0x143, 0,
                expected, 2, 1000, out _));
            Native.SendMessageW(combo, 0x14e, 0, 0);
            host.Command = (window, command) =>
            {
                if (window == combo && (command >> 16) == 7)
                {
                    Interlocked.Increment(ref expansions);
                    if (closeOnExpand)
                    {
                        Native.SendMessageW(combo, 0x14f, 0, 0);
                    }
                }
            };
        }, initializeOle: true);
        Assert.Equal(expected, await UiaSelection.ReadAsync(
            fixture.Root, 1, 0, default));
        Assert.Equal(0, Volatile.Read(ref expansions));
        Assert.Equal((nint)0, Native.SendMessageW(combo, 0x147, 0, 0));
        Assert.Equal((nint)0, Native.SendMessageW(combo, 0x157, 0, 0));
    }
}
