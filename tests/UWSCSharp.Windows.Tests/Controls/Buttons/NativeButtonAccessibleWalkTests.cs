using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonAccessibleApiTests
{
    [Fact]
    public void WalkMatchesSelfBeforeEnumeratingChildren()
    {
        using var node = new Fixture { StateValue = NativeButtonVariant.Child(16) };
        Assert.True(NativeButtonAccessibleWalk.Read(node.Pointer,
            new Native.Point(0, 0), default, out int role, out int state));
        Assert.Equal((44, 1), (role, state));
        Assert.Equal(new[] { "role:0", "state:0", "location:0" }, node.Events);
    }

    [Fact]
    public void WalkContinuesAfterFailedChildAndReleasesFailureOutput()
    {
        using var rejected = new Fixture();
        using var match = new Fixture { StateValue = NativeButtonVariant.Child(32) };
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = 0,
            CountValue = 3,
            ChildProvider = index => index == 1
                ? (unchecked((int)0x80004005), rejected.Pointer) : (0, match.Pointer)
        };
        Assert.True(NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 0), default, out int role, out int state));
        Assert.Equal((44, 2), (role, state));
        Assert.Equal(new[] { "role:0", "child:1", "child:2" }, root.Events);
        Assert.Equal(new[] { "add", "release" }, rejected.Events);
        Assert.Equal(1, match.References);
    }

    [Fact]
    public void WalkRecursesThroughContainersAndStopsAtFirstMatch()
    {
        using var match = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        using var inner = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = 0,
            CountValue = 1,
            ChildProvider = _ => (0, match.Pointer)
        };
        using var outer = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = 0,
            CountValue = 2,
            ChildProvider = _ => (0, inner.Pointer)
        };
        Assert.True(NativeButtonAccessibleWalk.Read(outer.Pointer,
            new Native.Point(0, 0), default, out _, out _));
        Assert.Single(outer.Events, value => value.StartsWith("child:"));
        Assert.Equal(1, inner.References);
        Assert.Equal(1, match.References);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void WalkConsumesDispatchEvenWhenNextReportsNoValue(int status)
    {
        using var match = new Fixture { StateValue = NativeButtonVariant.Child(16) };
        using var sequence = new Fixture
        {
            Result = status,
            NextProvider = (nint self, uint count, ref NativeButtonVariant value, out uint fetched) =>
            {
                fetched = 0;
                Marshal.AddRef(match.Pointer);
                value = new NativeButtonVariant { Type = 9, Pointer = match.Pointer };
                return status;
            }
        };
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = sequence.Pointer,
            CountValue = 2
        };
        Assert.True(NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 0), default, out int role, out int state));
        Assert.Equal((44, 1), (role, state));
        Assert.Equal(1, sequence.References);
        Assert.Equal(1, match.References);
        Assert.Single(sequence.Events, value => value == "next:1");
        Assert.Equal(new[] { "add", "add", "release", "add", "role:0",
            "state:0", "location:0", "release", "release" }, match.Events);
    }

    [Fact]
    public void WalkPreservesUnwrittenNextValueAndUsesChildCountLimit()
    {
        using var sequence = new Fixture { Result = 1 };
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = sequence.Pointer,
            CountValue = 3,
            ChildProvider = _ => (0, 0)
        };
        Assert.False(NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 0), default, out _, out _));
        Assert.Equal(new[] { "role:0", "child:0", "child:0", "child:0" }, root.Events);
        Assert.Equal(3, sequence.Events.Count(value => value == "next:1"));
    }

    [Fact]
    public void WalkCatchesChildRectangleOverflowAndContinues()
    {
        using var overflowing = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Bounds = (0, 0, 1, int.MaxValue)
        };
        // 上端を1にして下端の加算だけをoverflowさせます。
        using var bad = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Bounds = (0, 1, 1, int.MaxValue)
        };
        int retainedAtNextChild = 0;
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = 0,
            CountValue = 2,
            ChildProvider = index =>
            {
                if (index == 2)
                {
                    retainedAtNextChild = bad.References;
                }

                return (0, index == 1 ? bad.Pointer : overflowing.Pointer);
            }
        };
        Assert.True(NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 1), default, out _, out _));
        Assert.Equal(1, bad.References);
        Assert.Equal(2, retainedAtNextChild);
        Assert.Equal(new[] { "add", "add", "role:0", "state:0", "location:0",
            "release", "release" }, bad.Events);
    }

    [Fact]
    public void WalkPropagatesRootFailureOutsideChildCatch()
    {
        using var root = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Bounds = (0, 1, 1, int.MaxValue)
        };
        Assert.Throws<OverflowException>(() => NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 1), default, out _, out _));
    }

    [Fact]
    public void WalkPropagatesCancellationFromChildAndReleasesReferences()
    {
        using var cancellation = new CancellationTokenSource();
        using var child = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Selected = cancellation.Cancel
        };
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = 0,
            CountValue = 2,
            ChildProvider = _ => (0, child.Pointer)
        };
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
            NativeButtonAccessibleWalk.Act(root.Pointer, new Native.Point(0, 0),
                new NodePlatform(root.Events), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, child.References);
        Assert.DoesNotContain("child:2", root.Events);
        Assert.DoesNotContain("action:0", child.Events);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(0x400c)]
    [InlineData(8)]
    public void DispatchConversionRejectsOtherStandardTypesWithoutReplacing(int type)
    {
        using var fixture = new Fixture();
        nint destination = 0;
        NativeButtonAccessibleApi.Assign(ref destination, fixture.Pointer);
        try
        {
            Assert.Throws<InvalidCastException>(() =>
                NativeButtonAccessibleWalk.AssignDispatch(ref destination,
                    new NativeButtonVariant { Type = (ushort)type }));
            Assert.Equal(fixture.Pointer, destination);
            Assert.Equal(2, fixture.References);
        }
        finally
        {
            NativeButtonAccessibleApi.Release(ref destination);
        }
    }

    [Fact]
    public void DispatchConversionOwnsByrefValueAndEmptyReleasesDestination()
    {
        using var fixture = new Fixture();
        nint indirect = Marshal.AllocCoTaskMem(IntPtr.Size);
        nint destination = 0;
        try
        {
            Marshal.WriteIntPtr(indirect, fixture.Pointer);
            NativeButtonAccessibleWalk.AssignDispatch(ref destination,
                new NativeButtonVariant { Type = 0x4009, Pointer = indirect });
            Assert.Equal(2, fixture.References);
            NativeButtonAccessibleWalk.AssignDispatch(ref destination, default);
            Assert.Equal(0, destination);
            Assert.Equal(1, fixture.References);
        }
        finally
        {
            NativeButtonAccessibleApi.Release(ref destination);
            Marshal.FreeCoTaskMem(indirect);
        }
    }

    [Fact]
    public void WalkSkipsInvalidVariantThenContinuesToNextChild()
    {
        int next = 0;
        using var match = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        using var sequence = new Fixture
        {
            NextProvider = (nint self, uint count, ref NativeButtonVariant value, out uint fetched) =>
            {
                fetched = 0;
                value = next++ == 0
                    ? new NativeButtonVariant { Type = 1 }
                    : NativeButtonVariant.Child(7);
                return 1;
            }
        };
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = sequence.Pointer,
            CountValue = 2,
            ChildProvider = _ => (0, match.Pointer)
        };
        Assert.True(NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 0), default, out _, out _));
        Assert.Equal(2, next);
        Assert.Equal(new[] { "role:0", "child:7" }, root.Events);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void WalkIgnoresCountOnFailureAndReleasesEnumerator(int result)
    {
        using var sequence = new Fixture();
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = sequence.Pointer,
            CountValue = 3,
            Result = result
        };
        Assert.False(NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 0), default, out _, out _));
        Assert.Equal(new[] { "add", "reset", "release" }, sequence.Events);
    }

    [Fact]
    public void WalkReleasesLastFailedChildOutputOnExit()
    {
        using var rejected = new Fixture();
        using var root = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            Enumerator = 0,
            CountValue = 1,
            ChildProvider = _ => (1, rejected.Pointer)
        };
        Assert.False(NativeButtonAccessibleWalk.Read(root.Pointer,
            new Native.Point(0, 0), default, out _, out _));
        Assert.Equal(new[] { "add", "release" }, rejected.Events);
    }
}
