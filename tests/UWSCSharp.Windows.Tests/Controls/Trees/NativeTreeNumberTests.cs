using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeNumberTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RecursionReceivesCountButDoesNotReturnChildIncrements(int number)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint first = TreeControlTests.Add(tree, 0, "target");
        nint child = TreeControlTests.Add(tree, first, "target");
        TreeControlTests.Add(tree, 0, "target");
        using ToolbarClickTextTests.Interceptor hook = Flags(tree);
        Assert.Equal((number < 3, number < 3), NativeTreeControl.Click(tree,
            "target", true, default, platform: new OwnedListSelectionPlatform(), number: number));
        Assert.Equal(number < 2 ? first : number == 2 ? child : 0,
            Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public void ChildMatchesInDifferentBranchesDoNotAccumulate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        TreeControlTests.Add(tree, TreeControlTests.Add(tree, 0, "one"), "target");
        TreeControlTests.Add(tree, TreeControlTests.Add(tree, 0, "two"), "target");
        using ToolbarClickTextTests.Interceptor hook = Flags(tree);
        Assert.Equal((false, false), NativeTreeControl.Click(tree, "target", true,
            default, platform: new OwnedListSelectionPlatform(), number: 2));
        Assert.Equal(0, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicNumberAppliesToSingleNameAndLaterPathSegments(bool path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint parent = path ? TreeControlTests.Add(tree, 0, "parent") : 0;
        TreeControlTests.Add(tree, parent, "target");
        nint second = TreeControlTests.Add(tree, parent, "target");
        if (path)
        {
            TreeControlTests.Add(tree, TreeControlTests.Add(tree, 0, "parent"), "target");
        }

        ViewCheckTests.SetState(tree, second, false, 2);
        using ToolbarClickTextTests.Interceptor hook = Flags(tree);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        string label = path ? "parent\\target" : "target";
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(label), Value.Numeric(16), Value.Numeric(1), Value.Numeric(2)], default));
        Assert.Equal(second, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String(label), Value.Numeric(2)], default));
        Assert.Equal(second, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    private static ToolbarClickTextTests.Interceptor Flags(nint tree) =>
        new(tree, (_, _, _) => null, after: (message, _, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
            {
                // 展開済みとして固定し、番号だけを検証します。
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8, 0x20);
            }
        });
}
