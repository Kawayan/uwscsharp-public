using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreePathSplitTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("\\", "")]
    [InlineData("parent\\child\\", "parent|child")]
    [InlineData(" parent \\ child ", "parent |child")]
    [InlineData("parent\\\\name\\child", "parent\\name|child")]
    [InlineData("parent\\ \\child", "parent|child")]
    [InlineData("\\\\", "￥")]
    public void SplitsWithOriginalEscapeAndRemainderTrimming(string input, string expected)
    {
        Assert.Equal(expected, string.Join("|", NativeTreePath.Split(input)));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    public void MaximumIndexAllowsThirtyThreeSegments(int count)
    {
        string input = string.Join("\\", Enumerable.Repeat("item", count));
        if (count > 33)
        {
            Assert.Throws<InvalidOperationException>(() => NativeTreePath.Split(input));
        }
        else
        {
            Assert.Equal(count, NativeTreePath.Split(input).Length);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapedNameInMultiSegmentPathAndTrailingSeparator(bool trailing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint parent = TreeControlTests.Add(tree, 0, "pa\\rent");
        nint child = TreeControlTests.Add(tree, parent, "child");
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (_, _, _) => null,
            after: (message, _, pointer) =>
            {
                if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
                {
                    Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8, 0x20);
                }
            });
        string label = "pa\\\\rent\\child" + (trailing ? "\\" : "");
        Assert.Equal((true, true), NativeTreeControl.Click(tree, label, true,
            default, platform: new OwnedListSelectionPlatform()));
        Assert.Equal(child, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }
}
