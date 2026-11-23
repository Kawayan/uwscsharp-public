using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeSelectiveInputWindowTreeTests
{
    [Fact]
    public void VisibleRootAndChildResolveUsingNativeTree()
    {
        using var dpi = new PhysicalDpiScope();
        using var owner = new ButtonControlTests.Fixture();
        nint child = owner.Child("STATIC", "対象探索", 0, 10);
        var tree = NativeSelectiveInputWindowTree.Capture();
        Assert.True(tree.IsVisibleTopLevel(owner.Root));
        Assert.False(tree.IsVisibleTopLevel(child));
        Assert.Equal(owner.Root, tree.GetParent(child));
        Assert.True(tree.HasArea(child));
        Assert.Equal(owner.Root, SelectiveInputTargetResolver.Resolve(child, tree));
    }

    [Fact]
    public void VisibleOwnedPopupStopsBeforeItsOwner()
    {
        using var dpi = new PhysicalDpiScope();
        using var owner = new ButtonControlTests.Fixture();
        nint popup = CreateWindowExW(0, "STATIC", "所有ポップアップ",
            0x80000000, 30, 30, 120, 80, owner.Root, 0, 0, 0);
        Assert.NotEqual(0, popup);
        try
        {
            Native.ShowWindow(popup, 4);
            var tree = NativeSelectiveInputWindowTree.Capture();
            Assert.Equal(owner.Root, tree.GetParent(popup));
            Assert.True(tree.IsVisibleTopLevel(popup));
            Assert.Equal(popup, SelectiveInputTargetResolver.Resolve(popup, tree));
        }
        finally
        {
            Assert.True(Native.DestroyWindow(popup));
        }
    }

    [Fact]
    public void VisibilityChangesRequireAnotherCapture()
    {
        using var owner = new ButtonControlTests.Fixture();
        var visible = NativeSelectiveInputWindowTree.Capture();
        Assert.True(visible.IsVisibleTopLevel(owner.Root));
        Native.ShowWindow(owner.Root, 0);
        Assert.False(Native.IsWindowVisible(owner.Root));
        var hidden = NativeSelectiveInputWindowTree.Capture();
        Assert.True(visible.IsVisibleTopLevel(owner.Root));
        Assert.False(hidden.IsVisibleTopLevel(owner.Root));
        Native.ShowWindow(owner.Root, 4);
        Assert.True(Native.IsWindowVisible(owner.Root));
        Assert.False(hidden.IsVisibleTopLevel(owner.Root));
        Assert.True(NativeSelectiveInputWindowTree.Capture().IsVisibleTopLevel(owner.Root));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(20, 0)]
    [InlineData(0, 0)]
    public void ZeroExtentParentStopsAtItsChild(int width, int height)
    {
        using var dpi = new PhysicalDpiScope();
        using var owner = new ButtonControlTests.Fixture();
        nint parent = owner.Child("STATIC", "面積ゼロの親", 0, 10,
            width: width, height: height);
        nint child = CreateWindowExW(0, "STATIC", "子", 0x50000000,
            0, 0, 10, 10, parent, 0, 0, 0);
        Assert.NotEqual(0, child);
        var tree = NativeSelectiveInputWindowTree.Capture();
        Assert.Equal(parent, tree.GetParent(child));
        Assert.False(tree.HasArea(parent));
        Assert.Equal(child, SelectiveInputTargetResolver.Resolve(child, tree));
    }

    [Fact]
    public void InvisibleParentIsTraversedToVisibleRoot()
    {
        using var dpi = new PhysicalDpiScope();
        using var owner = new ButtonControlTests.Fixture();
        nint parent = owner.Child("STATIC", "非表示の親", 0, 10);
        nint child = CreateWindowExW(0, "STATIC", "子", 0x50000000,
            0, 0, 10, 10, parent, 0, 0, 0);
        Assert.NotEqual(0, child);
        Native.ShowWindow(parent, 0);
        Assert.False(Native.IsWindowVisible(parent));
        var tree = NativeSelectiveInputWindowTree.Capture();
        Assert.False(tree.IsVisibleTopLevel(parent));
        Assert.True(tree.HasArea(parent));
        Assert.Equal(parent, tree.GetParent(child));
        Assert.Equal(owner.Root, tree.GetParent(parent));
        Assert.Equal(owner.Root, SelectiveInputTargetResolver.Resolve(child, tree));
    }

    [Fact]
    public void FailedBoundsAreNotReportedAsZeroArea()
    {
        var tree = NativeSelectiveInputWindowTree.Capture();
        Assert.Throws<Win32Exception>(() => tree.HasArea(0));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle,
        string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu,
        nint instance, nint parameter);
}
