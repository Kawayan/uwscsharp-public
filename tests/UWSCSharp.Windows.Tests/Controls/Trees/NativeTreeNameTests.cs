using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeNameTests
{
    [Theory]
    [InlineData("&Alpha", "Alpha", false, false)]
    [InlineData("Alpha:", "Alpha", false, false)]
    [InlineData("Alpha(A)", "Alpha", false, false)]
    [InlineData("Alpha\tBeta", "Alpha", false, false)]
    [InlineData("Alpha\tBeta", "alpha\tBeta", false, true)]
    [InlineData("&Alpha", "&alpha", false, true)]
    [InlineData("  Alpha  ", " alpha ", false, true)]
    [InlineData("ＡＢＣ", "ABC", false, true)]
    [InlineData("ä", "Ä", false, false)]
    [InlineData("prefix Alpha suffix", "alpha", true, true)]
    [InlineData("prefix Alpha suffix", "alpha", false, false)]
    [InlineData("Alpha", " alpha", true, false)]
    [InlineData("Alpha", "", true, true)]
    public void LiteralNamesAndShortUseTreeContract(string itemName,
        string desired, bool partial, bool found)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint item = TreeControlTests.Add(tree, 0, itemName);
        Assert.Equal((found, found), NativeTreeControl.Click(tree, desired, true,
            default, kind: partial ? 272 : 16, platform: new OwnedListSelectionPlatform()));
        Assert.Equal(found ? item : 0, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionUsesShortForEveryPathSegment(bool partial)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint parent = TreeControlTests.Add(tree, 0, "&Parent:");
        nint child = TreeControlTests.Add(tree, parent, "Child\tExtra");
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (_, _, _) => null,
            after: (message, _, pointer) =>
            {
                if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
                {
                    Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8, 0x20);
                }
            });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        string name = partial ? "parent\\child" : "&parent:\\child\tExtra";
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(name), Value.Numeric(partial ? 272 : 16)], default));
        Assert.Equal(child, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }
}
