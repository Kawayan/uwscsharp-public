using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ProviderCleanupTests
{
    private sealed class Operations : ProviderCleanupOperations
    {
        internal readonly List<string> Calls = [];
        internal readonly Dictionary<string, Exception> Failures = [];
        internal int ClearStatus;

        internal override bool IsComObject(object value) => value is string;

        internal override void Release(object value) => Call("object:" + value);

        internal override void ReleasePointer(nint value) => Call("pointer:" + value);

        internal override int ClearVariant(nint value)
        {
            Call("variant:" + value);
            return ClearStatus;
        }

        internal override void Free(nint value) => Call("free:" + value);

        private void Call(string name)
        {
            Calls.Add(name);
            if (Failures.TryGetValue(name, out Exception? failure))
            {
                throw failure;
            }
        }
    }

    private static void Run(Exception? primary, Operations operations)
    {
        Exception? captured = null;
        try
        {
            if (primary is not null)
            {
                throw primary;
            }
        }
        catch (Exception failure)
        {
            captured = failure;
            throw;
        }
        finally
        {
            var cleanup = new ProviderCleanup(captured, operations);
            cleanup.Release("second");
            cleanup.Release("first");
            cleanup.ReleasePointer(7);
            cleanup.ClearVariant(24);
            cleanup.ClearVariant(0);
            cleanup.Free(100);
            cleanup.Finish();
        }
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("callback")]
    [InlineData("cancel")]
    public void AllCleanupStagesKeepPrimaryAndContinue(string kind)
    {
        using var cancel = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "operation" => new ScriptException(7, "操作失敗"),
            "cancel" => new OperationCanceledException(cancel.Token),
            _ => new InvalidOperationException("callback障害")
        };
        var operations = new Operations();
        var first = new InvalidOperationException("参照解放失敗");
        var second = new InvalidOperationException("メモリ解放失敗");
        operations.Failures.Add("object:second", first);
        operations.Failures.Add("free:100", second);
        Exception actual = Assert.ThrowsAny<Exception>(() => Run(primary, operations));
        Assert.Same(primary, actual);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            primary.Data[ProviderCleanup.DataKey]);
        Assert.Equal([first, second], cleanup.InnerExceptions);
        Assert.Equal(["object:second", "object:first", "pointer:7",
            "variant:24", "variant:0", "free:100"], operations.Calls);
        if (primary is OperationCanceledException cancelled)
        {
            Assert.Equal(cancel.Token, cancelled.CancellationToken);
        }
    }

    [Fact]
    public void CleanupOnlyFailureIsAnUnclassifiedHostAggregate()
    {
        var operations = new Operations();
        var failure = new InvalidOperationException("release");
        operations.Failures.Add("object:second", failure);
        AggregateException actual = Assert.Throws<AggregateException>(() => Run(null, operations));
        Assert.Same(failure, Assert.Single(actual.InnerExceptions));
        Assert.Equal(6, operations.Calls.Count);
        Assert.False(WindowsOperationErrors.Contains(actual));
    }

    [Fact]
    public void VariantClearHresultIsCheckedAndRemainingCleanupContinues()
    {
        var primary = new InvalidOperationException("body");
        var operations = new Operations { ClearStatus = unchecked((int)0x8002000d) };
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => Run(primary, operations)));
        AggregateException cleanup = Assert.IsType<AggregateException>(
            primary.Data[ProviderCleanup.DataKey]);
        Assert.Equal(2, cleanup.InnerExceptions.Count);
        Assert.All(cleanup.InnerExceptions,
            failure => Assert.Equal(unchecked((int)0x8002000d), failure.HResult));
        Assert.Equal("free:100", operations.Calls[^1]);
    }

    [Fact]
    public void NestedScopesAppendInsteadOfReplacingEarlierCleanupFailures()
    {
        var primary = new ScriptException(3, "body");
        var inner = new Operations();
        var outer = new Operations();
        var first = new InvalidOperationException("inner");
        var second = new InvalidOperationException("outer");
        inner.Failures.Add("object:one", first);
        outer.Failures.Add("object:one", second);
        var innerCleanup = new ProviderCleanup(primary, inner);
        innerCleanup.Release("one");
        innerCleanup.Finish();
        var outerCleanup = new ProviderCleanup(primary, outer);
        outerCleanup.Release("one");
        outerCleanup.Finish();
        AggregateException cleanup = Assert.IsType<AggregateException>(
            primary.Data[ProviderCleanup.DataKey]);
        Assert.Equal([first, second], cleanup.InnerExceptions);
    }

    [Fact]
    public void ComOnlyReleaseSkipsManagedAndNullValues()
    {
        var operations = new Operations();
        var cleanup = new ProviderCleanup(null, operations);
        cleanup.ReleaseComOnly(null);
        cleanup.ReleaseComOnly(42);
        cleanup.ReleaseComOnly("reference");
        cleanup.Finish();
        Assert.Equal(["object:reference"], operations.Calls);
    }

    [Fact]
    public async Task FaultInjectionFlowsToWorkerAndRestoresNestedContext()
    {
        ProviderCleanupOperations before = ProviderCleanupOperations.Current;
        var first = new Operations();
        var second = new Operations();
        using (ProviderCleanupOperations.UseForTesting(first))
        {
            Assert.Same(first, await Task.Run(() => ProviderCleanupOperations.Current));
            using (ProviderCleanupOperations.UseForTesting(second))
            {
                Assert.Same(second, ProviderCleanupOperations.Current);
            }

            Assert.Same(first, ProviderCleanupOperations.Current);
        }

        Assert.Same(before, ProviderCleanupOperations.Current);
    }
}
