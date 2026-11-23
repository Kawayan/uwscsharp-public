using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreePathTests
{
    private sealed class Platform : OwnedListSelectionPlatform
    {
        internal List<nint> Selected { get; } = [];

        public override nint Send(nint target, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            if (message == 0)
            {
                return 1;
            }

            if (message == 0x110b)
            {
                Selected.Add(data);
            }

            return base.Send(target, message, parameter, data, timed, timeout, flags);
        }

        public override void Sleep(int milliseconds, CancellationToken token) =>
            token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token) => token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EachSegmentSelectsBeforeReadingChildren(bool query, bool missing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint parent = TreeControlTests.Add(tree, 0, "parent");
        nint child = TreeControlTests.Add(tree, parent, "child");
        TreeControlTests.Add(tree, child, "deep");
        var platform = new Platform();
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (message, _, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
            {
                nint item = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 8 : 4);
                if (item == child)
                {
                    Assert.Equal(new[] { parent }, platform.Selected);
                }
            }

            return null;
        }, after: (message, _, pointer) =>
        {
            if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
            {
                // getter後に展開済みflagを設定し、キー送信の有無を固定します。
                Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8, 0x20);
            }
        });
        string label = missing ? "parent\\deep" : "parent\\child";
        (bool found, bool success) = NativeTreeControl.Click(tree, label, true,
            default, platform: platform, readCheck: query ? _ => { }
        : null);
        Assert.Equal(!missing, found);
        Assert.Equal(!missing, success);
        nint[] expected = missing ? [parent, 0] : [parent, child, child];
        Assert.Equal(expected, platform.Selected);
        Assert.Equal(missing ? 0 : child,
            Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("parent\\child", false, true)]
    [InlineData("parent\\child", true, true)]
    [InlineData("\\parent\\child", true, false)]
    public void FirstSegmentPrefersRootSiblingsBeforeRecursiveFallback(
        string label, bool removeRootMatch, bool found)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint other = TreeControlTests.Add(tree, 0, "other");
        nint nested = TreeControlTests.Add(tree, other, "parent");
        nint nestedChild = TreeControlTests.Add(tree, nested, "child");
        nint root = TreeControlTests.Add(tree, 0, removeRootMatch ? "different" : "parent");
        nint rootChild = TreeControlTests.Add(tree, root, "child");
        var platform = new Platform();
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (_, _, _) => null,
            after: (message, _, pointer) =>
            {
                if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
                {
                    Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8, 0x20);
                }
            });
        Assert.Equal((found, found), NativeTreeControl.Click(tree, label, true,
            default, platform: platform));
        Assert.Equal(found ? removeRootMatch ? nestedChild : rootChild : 0,
            Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Equal(found ? 3 : 0, platform.Selected.Count);
        Assert.Null(fixture.CallbackFailure);
    }
}
