using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AutomaticAccessibleKindTests
{
    [ComVisible(true)]
    [Guid("00000114-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleWindow
    {
        void GetWindow(out nint window);

        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class WindowProvider : AccessibleTestProvider, IOleWindow
    {
        private readonly nint window;

        internal WindowProvider(nint window)
            : base(33, 0, "", "", new AccessibleTestProvider(34, 0, "target", ""))
        {
            this.window = window;
        }

        public void GetWindow(out nint result) => result = window;

        public void ContextSensitiveHelp(bool enter)
        {
        }
    }

    [Theory]
    [InlineData("SysListView32", 32, true)]
    [InlineData("SysListView32", 2, false)]
    [InlineData("LISTBOX", 2, true)]
    [InlineData("LISTBOX", 32, false)]
    public void NativeWindowDisambiguatesListRole(string controlClass,
        int kind, bool expected)
    {
        SliderControlTests.InitializeControls(1);
        using var fixture = new ButtonControlTests.Fixture();
        nint window = fixture.Child(controlClass, "", 0, 10);
        var provider = new WindowProvider(window);
        int actions = 0;
        List<string> result = AccessibleItems.ReadProvider(provider, kind,
            false, true, 1, default, 7, 1, "target",
            write: (_, _, _) => actions++, automaticAction: true);
        Assert.Equal(expected ? 1 : 0, actions);
        Assert.Equal(expected ? new[] { "1" } : [], result);
    }

    [Theory]
    [InlineData(22, 43, 64, true)]
    [InlineData(22, 43, 1, true)]
    [InlineData(33, 34, 2, true)]
    [InlineData(33, 34, 32, false)]
    [InlineData(24, 34, 32, true)]
    [InlineData(24, 34, 2, false)]
    [InlineData(24, 28, 32, true)]
    [InlineData(24, 29, 32, true)]
    [InlineData(33, 44, 2, true)]
    [InlineData(24, 44, 32, true)]
    [InlineData(33, 43, 2, false)]
    [InlineData(24, 43, 32, false)]
    public void ContainerRoleClassifiesItemsWithoutTreatingNestedPushAsAnItem(
        int parentRole, int itemRole, int kind, bool expected)
    {
        var provider = new AccessibleTestProvider(parentRole, 0, "", "",
            new AccessibleTestProvider(itemRole, 0, "target", "target"));
        int actions = 0;
        List<string> result = AccessibleItems.ReadProvider(provider, kind,
            false, true, 1, default, 7, 1, "target",
            write: (_, _, _) => actions++, automaticAction: true);
        Assert.Equal(expected ? 1 : 0, actions);
        Assert.Equal(expected ? new[] { "1" } : [], result);
    }

    [Theory]
    [InlineData(43, 1, true)]
    [InlineData(43, 2, false)]
    [InlineData(43, 4, false)]
    [InlineData(43, 8, false)]
    [InlineData(43, 16, false)]
    [InlineData(43, 32, false)]
    [InlineData(43, 64, false)]
    [InlineData(34, 2, true)]
    [InlineData(34, 1, false)]
    [InlineData(36, 16, true)]
    [InlineData(36, 2, false)]
    [InlineData(37, 8, true)]
    [InlineData(12, 4, true)]
    [InlineData(51, 0, false)]
    [InlineData(52, 0, false)]
    public void AutomaticSearchUsesRequestedKind(int role, int kind, bool expected)
    {
        var provider = new AccessibleTestProvider(role, 0, "target", "");
        int actions = 0;
        List<string> result = AccessibleItems.ReadProvider(provider, kind,
            false, true, 1, default, 7, 1, "target",
            write: (_, _, _) => actions++, automaticAction: true);
        Assert.Equal(expected ? 1 : 0, actions);
        Assert.Equal(expected ? new[] { "1" } : [], result);
    }

    [Fact]
    public void WrongKindDoesNotConsumeNumberOrSuppressSamePositionCandidate()
    {
        var provider = new AccessibleTestProvider(10, 0, "", "",
            new(43, 0, "target", ""), new(36, 0, "target", ""));
        int role = 0;
        List<string> result = AccessibleItems.ReadProvider(provider,
            (int)ClickItemFlags.TreeView, false, true, 1, default, 7, 1, "target",
            write: (item, child, _) => role = (int)item.Role(child)!,
            automaticAction: true);
        Assert.Equal(36, role);
        Assert.Equal(new[] { "1" }, result);
    }

    [Fact]
    public void ExplicitAccessibleSearchKeepsGenericRoleContract()
    {
        var provider = new AccessibleTestProvider(43, 0, "target", "");
        int actions = 0;
        Assert.Equal(new[] { "1" }, AccessibleItems.ReadProvider(provider,
            (int)(ClickItemFlags.Accessible | ClickItemFlags.List),
            false, true, 1, default, 7, 1, "target", write: (_, _, _) => actions++));
        Assert.Equal(1, actions);
    }
}
