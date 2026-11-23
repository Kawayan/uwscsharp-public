using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonAccessibleApiTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(unchecked((int)0x80004005), false)]
    public void SessionPointResultControlsCleanupTiming(int result, bool found)
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(16) };
        using var variant = new Fixture();
        var source = new SessionSource(fixture, variant) { PointResult = result };
        var session = new NativeButtonAccessibleSession(source);
        try
        {
            int role = 91;
            Assert.Equal(found, session.ReadPoint(new Native.Point(0, 0),
                ref role, out int state, default));
            Assert.Equal(found ? 44 : 91, role);
            Assert.Equal(found ? 1 : 0, state);
            Assert.Equal(found ? 1 : 2, fixture.References);
            Assert.Equal(found ? 1 : 2, variant.References);
            if (!found)
            {
                source.BeforeRoot = () =>
                {
                    Assert.Equal(1, fixture.References);
                    Assert.Equal(2, variant.References);
                };
                session.ReadRoot(123, new Native.Point(0, 0),
                    ref role, out state, default);
                Assert.Equal(44, role);
                Assert.Equal(1, state);
                Assert.Equal(1, fixture.References);
                Assert.Equal(2, variant.References);
                Assert.Equal((nint)123, source.Root);
            }
        }
        finally
        {
            session.Dispose();
            session.Dispose();
        }

        Assert.Equal(1, fixture.References);
        Assert.Equal(1, variant.References);
    }

    [Fact]
    public void SessionPointMissClearsBeforeFallbackAndPreservesRole()
    {
        using var fixture = new Fixture { RoleValue = NativeButtonVariant.Child(42) };
        using var variant = new Fixture();
        using var session = new NativeButtonAccessibleSession(new SessionSource(fixture, variant));
        int role = 91;
        Assert.False(session.ReadPoint(new Native.Point(0, 0), ref role, out _, default));
        Assert.Equal(91, role);
        Assert.Equal(1, fixture.References);
        Assert.Equal(1, variant.References);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(unchecked((int)0x80004005), false)]
    [InlineData(0, true)]
    public void SessionRootUsesOutPointerRegardlessOfHResult(int result, bool empty)
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(32) };
        using var variant = new Fixture();
        var source = new SessionSource(fixture, variant) { RootResult = result, EmptyRoot = empty };
        using var session = new NativeButtonAccessibleSession(source);
        int role = 91;
        session.ReadRoot(123, new Native.Point(0, 0), ref role, out int state, default);
        Assert.Equal(empty ? 91 : 44, role);
        Assert.Equal(empty ? 0 : 2, state);
        Assert.Equal(1, fixture.References);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionCancellationReleasesWrittenOutValues(bool root)
    {
        using var fixture = new Fixture();
        using var variant = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var source = new SessionSource(fixture, variant) { AfterAcquire = cancellation.Cancel };
        using (var session = new NativeButtonAccessibleSession(source))
        {
            int role = 91;
            Assert.Throws<OperationCanceledException>(() =>
            {
                if (root)
                {
                    session.ReadRoot(123, new Native.Point(0, 0),
                        ref role, out _, cancellation.Token);
                }
                else
                {
                    session.ReadPoint(new Native.Point(0, 0),
                        ref role, out _, cancellation.Token);
                }
            });
        }

        Assert.Equal(1, fixture.References);
        Assert.Equal(1, variant.References);
    }

    [Fact]
    public void SessionActionUsesRawWalkAndReleasesRoot()
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        using var variant = new Fixture();
        using var session = new NativeButtonAccessibleSession(new SessionSource(fixture, variant));
        Assert.True(session.ActRoot(123, new Native.Point(0, 0),
            new NodePlatform(fixture.Events), default));
        Assert.Contains("select:1:0", fixture.Events);
        Assert.Contains("action:0", fixture.Events);
        Assert.Equal(1, fixture.References);
    }

    [Fact]
    public void SessionNodeExceptionClearsVariantBeforeFinalPointerRelease()
    {
        using var fixture = new Fixture
        {
            StateValue = NativeButtonVariant.Child(0),
            Bounds = (int.MaxValue, 0, 1, 1)
        };
        using var variant = new Fixture();
        var released = new List<string>();
        fixture.OnRelease = () => released.Add("accessible");
        variant.OnRelease = () => released.Add("variant");
        using (var session = new NativeButtonAccessibleSession(new SessionSource(fixture, variant)))
        {
            int role = 91;
            Assert.Throws<OverflowException>(() => session.ReadPoint(
                new Native.Point(int.MaxValue, 0), ref role, out _, default));
            Assert.Equal(2, fixture.References);
            Assert.Equal(2, variant.References);
        }

        Assert.Equal(new[] { "variant", "accessible" }, released);
    }

    [Fact]
    public void SessionPointSuccessReleasesPointerBeforeVariant()
    {
        using var fixture = new Fixture { StateValue = NativeButtonVariant.Child(0) };
        using var variant = new Fixture();
        var released = new List<string>();
        fixture.OnRelease = () => released.Add("accessible");
        variant.OnRelease = () => released.Add("variant");
        using (var session = new NativeButtonAccessibleSession(new SessionSource(fixture, variant)))
        {
            int role = 91;
            Assert.True(session.ReadPoint(new Native.Point(0, 0), ref role, out _, default));
            Assert.Equal(new[] { "accessible", "variant" }, released);
        }

        Assert.Equal(2, released.Count);
    }

    [Fact]
    public void SessionNativeSourceReadsOwnedButtonClient()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "MSAA", 3, 10);
        using var source = new NativeButtonAccessibleSource();
        Assert.Equal(0, source.FromWindow(button, out nint accessible));
        Native.Point point;
        try
        {
            Assert.Equal(0, NativeButtonAccessibleApi.Location(accessible,
                NativeButtonVariant.Child(0), out int left, out int top, out _, out _));
            point = new Native.Point(left, top);
        }
        finally
        {
            NativeButtonAccessibleApi.Release(ref accessible);
        }

        using var session = new NativeButtonAccessibleSession(source);
        int role = 91;
        session.ReadRoot(button, point, ref role, out int state, default);
        Assert.Equal(44, role);
        Assert.Equal(0, state);
    }

    private sealed class SessionSource(Fixture accessible, Fixture variant) : IButtonAccessibleSource
    {
        internal int PointResult
        {
            get; init;
        }

        internal int RootResult
        {
            get; init;
        }

        internal bool EmptyRoot
        {
            get; init;
        }

        internal nint Root
        {
            get; private set;
        }

        internal Action? BeforeRoot
        {
            get; set;
        }

        internal Action? AfterAcquire
        {
            get; init;
        }

        public int FromPoint(Native.Point point, out nint result, ref NativeButtonVariant child)
        {
            result = accessible.Pointer;
            Marshal.AddRef(result);
            child = new NativeButtonVariant { Type = 9, Pointer = variant.Pointer };
            Marshal.AddRef(child.Pointer);
            AfterAcquire?.Invoke();
            return PointResult;
        }

        public int FromWindow(nint root, out nint result)
        {
            BeforeRoot?.Invoke();
            Root = root;
            result = EmptyRoot ? 0 : accessible.Pointer;
            if (result != 0)
            {
                Marshal.AddRef(result);
            }

            AfterAcquire?.Invoke();
            return RootResult;
        }
    }
}
