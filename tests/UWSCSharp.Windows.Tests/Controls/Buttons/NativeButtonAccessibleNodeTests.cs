using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonAccessibleApiTests
{
    [Theory]
    [InlineData(44, 0, 0)]
    [InlineData(44, 2, 0)]
    [InlineData(44, 8, 0)]
    [InlineData(44, 16, 1)]
    [InlineData(44, 48, 2)]
    [InlineData(43, 2, 1)]
    [InlineData(43, 8, 1)]
    [InlineData(43, 16, 1)]
    [InlineData(45, 2, 1)]
    [InlineData(45, 8, 1)]
    [InlineData(45, 32, 2)]
    [InlineData(43, 1, 0)]
    [InlineData(44, 0x8001, 0)]
    public void NodeUsesRoleSpecificStateMasks(int role, int flags, int expected)
    {
        using var fixture = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(role),
            StateValue = NativeButtonVariant.Child(flags)
        };
        Assert.True(NativeButtonAccessibleNode.Read(fixture.Pointer,
            NativeButtonVariant.Child(0), new Native.Point(0, 0), default,
            out int actualRole, out int actualState));
        Assert.Equal(role, actualRole);
        Assert.Equal(expected, actualState);
    }

    [Theory]
    [InlineData(-10, -20, true)]
    [InlineData(20, 20, true)]
    [InlineData(-11, 0, false)]
    [InlineData(0, -21, false)]
    [InlineData(21, 0, false)]
    [InlineData(0, 21, false)]
    public void NodeIncludesRectangleEdges(int x, int y, bool expected)
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        Assert.Equal(expected, NativeButtonAccessibleNode.Read(fixture.Pointer,
            NativeButtonVariant.Child(0), new Native.Point(x, y), default,
            out _, out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, unchecked((int)0x80004005))]
    [InlineData(1, unchecked((int)0x80004005))]
    [InlineData(2, unchecked((int)0x80004005))]
    public void NodeRejectsAnyNonzeroGetterHResult(int stage, int result)
    {
        using var fixture = new Fixture
        {
            RoleResult = stage == 0 ? result : 0,
            StateResult = stage == 1 ? result : 0,
            LocationResult = stage == 2 ? result : 0,
            StateValue = NativeButtonVariant.Child(0)
        };
        Assert.False(NativeButtonAccessibleNode.Read(fixture.Pointer,
            NativeButtonVariant.Child(0), new Native.Point(0, 0), default,
            out int role, out int state));
        Assert.Equal(0, role);
        Assert.Equal(0, state);
        Assert.Equal(stage + 1, fixture.Events.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NodeRejectsWrongTypeOrRole(int invalid)
    {
        using var fixture = new Fixture
        {
            RoleValue = invalid == 0
                ? new NativeButtonVariant { Type = 10, Integer = 44 }
                : NativeButtonVariant.Child(invalid == 1 ? 42 : 44),
            StateValue = invalid == 2
                ? new NativeButtonVariant { Type = 10, Integer = 16 }
                : NativeButtonVariant.Child(16)
        };
        Assert.False(NativeButtonAccessibleNode.Read(fixture.Pointer,
            NativeButtonVariant.Child(0), new Native.Point(0, 0), default,
            out _, out _));
        Assert.Equal(invalid == 2 ? 2 : 1, fixture.Events.Count);
    }

    [Fact]
    public void NodeClearsOwnedOutputEvenWhenGetterFails()
    {
        using var fixture = new Fixture { RoleResult = unchecked((int)0x80004005) };
        fixture.RoleValue = new NativeButtonVariant { Type = 9, Pointer = fixture.Pointer };
        Assert.False(NativeButtonAccessibleNode.Read(fixture.Pointer,
            NativeButtonVariant.Child(0), new Native.Point(0, 0), default,
            out _, out _));
        Assert.Equal(1, fixture.References);
        Assert.Equal(new[] { "role:0", "add", "release" }, fixture.Events);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void EmptyCheckUnwrapsByrefButPassesOriginalChildToProvider(int type, bool expected)
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        nint storage = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeButtonVariant>());
        try
        {
            var value = new NativeButtonVariant { Type = (ushort)type };
            Marshal.StructureToPtr(value, storage, false);
            Assert.Equal(expected, NativeButtonAccessibleNode.Read(fixture.Pointer,
                new NativeButtonVariant { Type = 0x400c, Pointer = storage },
                new Native.Point(0, 0), default, out _, out _));
            if (expected)
            {
                Assert.Equal((ushort)0x400c, fixture.SeenChildType);
            }
            else
            {
                Assert.Empty(fixture.Events);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(storage);
        }
    }

    [Fact]
    public void RectangleOverflowPropagatesAfterLeftAndTopChecks()
    {
        using var fixture = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Bounds = (int.MaxValue, 0, 1, 1)
        };
        Assert.False(NativeButtonAccessibleNode.Read(fixture.Pointer,
            NativeButtonVariant.Child(0), new Native.Point(0, 0), default,
            out _, out _));
        Assert.Throws<OverflowException>(() => NativeButtonAccessibleNode.Read(
            fixture.Pointer, NativeButtonVariant.Child(0),
            new Native.Point(int.MaxValue, 0), default, out _, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void ActionIgnoresOperationHResultsAndPreservesWaitOrder(int result)
    {
        using var fixture = new Fixture
        {
            Result = result,
            RoleResult = 0,
            StateResult = 0,
            LocationResult = 0,
            StateValue = NativeButtonVariant.Child(0)
        };
        var platform = new NodePlatform(fixture.Events);
        Assert.True(NativeButtonAccessibleNode.Act(fixture.Pointer,
            NativeButtonVariant.Child(0), new Native.Point(0, 0), platform, default));
        var expected = new List<string>
        {
            "role:0", "state:0", "location:0", "select:1:0", "sleep:20"
        };
        for (int i = 0; i < 20; i++)
        {
            expected.Add("sleep:0");
            expected.Add("pump");
        }

        expected.Add("action:0");
        Assert.Equal(expected, fixture.Events);
    }

    [Fact]
    public void CancellationAfterSelectPreventsDefaultAction()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Selected = cancellation.Cancel
        };
        var platform = new NodePlatform(fixture.Events);
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            NativeButtonAccessibleNode.Act(fixture.Pointer, NativeButtonVariant.Child(0),
                new Native.Point(0, 0), platform, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(new[] { "role:0", "state:0", "location:0", "select:1:0" },
            fixture.Events);
    }

    private sealed class NodePlatform(List<string> events) : OwnedListSelectionPlatform
    {
        public override void Sleep(int milliseconds, CancellationToken token)
            => events.Add("sleep:" + milliseconds);

        public override void Pump(CancellationToken token) => events.Add("pump");
    }
}
