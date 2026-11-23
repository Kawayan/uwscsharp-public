using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeButtonAccessibleApiTests
{
    private sealed class CleanupOperations : ProviderCleanupOperations
    {
        internal int Clears;
        internal int FailAfter;
        internal readonly List<int> Values = [];

        internal override int ClearVariant(ref NativeButtonVariant value)
        {
            Values.Add(value.Integer);
            int result = base.ClearVariant(ref value);
            return result == 0 && ++Clears > FailAfter
                ? unchecked((int)0x80004005) : result;
        }

        internal override void ReleasePointer(nint value)
        {
            base.ReleasePointer(value);
            throw new InvalidOperationException("解放後の注入失敗");
        }
    }

    [Fact]
    public void NodeOverflowKeepsPrimaryWhenBothVariantsFailCleanup()
    {
        using var fixture = new Fixture
        {
            StateValue = NativeButtonVariant.Child(16),
            Bounds = (int.MaxValue, 0, 1, 1)
        };
        var operations = new CleanupOperations();
        using IDisposable scope = ProviderCleanupOperations.UseForTesting(operations);
        OverflowException failure = Assert.Throws<OverflowException>(() =>
            NativeButtonAccessibleNode.Read(fixture.Pointer,
                NativeButtonVariant.Child(0), new Native.Point(int.MaxValue, 0),
                default, out _, out _));
        AggregateException cleanup = Assert.IsType<AggregateException>(
            failure.Data[ProviderCleanup.DataKey]);
        Assert.Equal(2, cleanup.InnerExceptions.Count);
        Assert.Equal([16, 44], operations.Values);
        Assert.Equal(1, fixture.References);
    }

    [Fact]
    public void SessionDisposalClearsVariantAndReleasesPointerWithoutReplacingPrimary()
    {
        using var fixture = new Fixture();
        using var variant = new Fixture();
        var session = new NativeButtonAccessibleSession(
            new SessionSource(fixture, variant) { PointResult = 1 });
        int role = 91;
        Assert.False(session.ReadPoint(default, ref role, out _, default));
        var primary = new InvalidOperationException("操作失敗");
        var operations = new CleanupOperations();
        using IDisposable scope = ProviderCleanupOperations.UseForTesting(operations);
        session.Dispose(primary);
        session.Dispose(primary);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            primary.Data[ProviderCleanup.DataKey]);
        Assert.Equal(2, cleanup.InnerExceptions.Count);
        Assert.Equal(1, operations.Clears);
        Assert.Equal(1, fixture.References);
        Assert.Equal(1, variant.References);
    }

    [Fact]
    public void WalkChecksFinalVariantClearWithoutChangingTraversalResults()
    {
        using var fixture = new Fixture
        {
            RoleValue = NativeButtonVariant.Child(42),
            CountValue = 0,
            Enumerator = 0
        };
        var operations = new CleanupOperations { FailAfter = 2 };
        using IDisposable scope = ProviderCleanupOperations.UseForTesting(operations);
        AggregateException cleanup = Assert.Throws<AggregateException>(() =>
            NativeButtonAccessibleWalk.Read(fixture.Pointer, default,
                default, out _, out _));
        Assert.Equal(unchecked((int)0x80004005),
            Assert.Single(cleanup.InnerExceptions).HResult);
        Assert.Equal(3, operations.Clears);
        Assert.Equal(1, fixture.References);
        Assert.False(WindowsOperationErrors.Contains(cleanup));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BackendKeepsReadOrActionFailureThroughSessionDisposal(
        bool action, bool cancellation)
    {
        using var fixture = new Fixture();
        using var variant = new Fixture();
        using var cancel = new CancellationTokenSource();
        Exception primary = cancellation
            ? new OperationCanceledException(cancel.Token)
            : new InvalidOperationException("取得途中の失敗");
        var source = new SessionSource(fixture, variant)
        {
            AfterAcquire = () => throw primary
        };
        using var backend = new NativeButtonAccessibleBackend(new BackendWindows(),
            new NativeButtonPointWindow(new BackendPoints()), source, _ => 0, _ => 0);
        var operations = new CleanupOperations();
        using IDisposable scope = ProviderCleanupOperations.UseForTesting(operations);
        int role = 91;
        Exception actual = Assert.ThrowsAny<Exception>(() =>
        {
            if (action)
            {
                backend.Act(1, 2, cancel.Token);
            }
            else
            {
                backend.Read(1, 2, cancel.Token, ref role, out _);
            }
        });
        Assert.Same(primary, actual);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            primary.Data[ProviderCleanup.DataKey]);
        Assert.Equal(2, cleanup.InnerExceptions.Count);
        Assert.Equal(1, fixture.References);
        Assert.Equal(1, variant.References);
        if (actual is OperationCanceledException cancelled)
        {
            Assert.Equal(cancel.Token, cancelled.CancellationToken);
        }
    }
}
