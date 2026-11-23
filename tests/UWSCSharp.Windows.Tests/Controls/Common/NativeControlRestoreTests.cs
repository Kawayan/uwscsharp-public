using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeControlOperationTests
{
    [Theory]
    [InlineData(false, "operation")]
    [InlineData(true, "operation")]
    [InlineData(false, "host")]
    [InlineData(true, "host")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "script")]
    [InlineData(true, "script")]
    public async Task RestoreFailureDoesNotReplacePrimaryOrCancellation(
        bool worker, string kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "operation" => WindowsOperationErrors.Record(
                new InvalidOperationException("操作失敗")),
            "cancel" => new OperationCanceledException(cancel.Token),
            "script" => new ScriptException(7, "script失敗"),
            _ => new ArgumentException("host障害")
        };
        var restore = new InvalidOperationException("復元失敗");
        var platform = new Platform { RestoreFailure = restore };
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => worker
            ? NativeControlOperation.RunAsync<bool>(10, platform,
                _ => throw primary, default)
            : Task.FromResult(NativeControlOperation.Execute<bool>(10, platform,
                _ => throw primary, default)));
        Assert.Same(primary, actual);
        AggregateException errors = Assert.IsType<AggregateException>(
            actual.Data["UWSCSharp.ControlRestoreErrors"]);
        Assert.Same(restore, Assert.Single(errors.InnerExceptions));
        Assert.Equal(["restore:77"], platform.Events);
        Assert.Equal(kind == "operation", WindowsOperationErrors.Contains(actual));
        if (actual is OperationCanceledException cancelled)
        {
            Assert.Equal(cancel.Token, cancelled.CancellationToken);
        }

        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }

    [Fact]
    public void NestedRestoresAppendFailuresToSamePrimary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var primary = new InvalidOperationException("処理失敗");
        var first = new ArgumentException("内側の復元失敗");
        var second = new NotSupportedException("外側の復元失敗");
        var inner = new Platform { RestoreFailure = first };
        var outer = new Platform { RestoreFailure = second };
        Exception actual = Assert.Throws<InvalidOperationException>(() =>
            NativeControlOperation.Execute(10, outer, _ =>
                NativeControlOperation.Execute<bool>(10, inner,
                    _ => throw primary, default), default));
        Assert.Same(primary, actual);
        AggregateException errors = Assert.IsType<AggregateException>(
            primary.Data["UWSCSharp.ControlRestoreErrors"]);
        Assert.Equal([first, second], errors.InnerExceptions);
        Assert.Equal(["restore:77"], inner.Events);
        Assert.Equal(["restore:77"], outer.Events);
    }

    [Fact]
    public async Task WorkerDeadlineRestoresAndKeepsDiagnosticOnOperationCause()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var restored = new ManualResetEventSlim();
        var restore = new InvalidOperationException("復元失敗");
        var platform = new Platform { RestoreFailure = restore, Restored = restored };
        OperationCanceledException? primary = null;
        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
            NativeControlOperation.RunAsync<bool>(10, platform, token =>
            {
                token.WaitHandle.WaitOne();
                primary = new OperationCanceledException(token);
                throw primary;
            }, default, TimeSpan.FromMilliseconds(100)));
        Assert.True(restored.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => primary?.Data[
            NativeControlOperation.RestoreErrorsKey] is AggregateException,
            TimeSpan.FromSeconds(5)));
        AggregateException errors = Assert.IsType<AggregateException>(
            primary!.Data[NativeControlOperation.RestoreErrorsKey]);
        Assert.Same(restore, Assert.Single(errors.InnerExceptions));
        Assert.True(WindowsOperationErrors.Contains(timeout));
        if (timeout.InnerException is not null)
        {
            Assert.Same(primary, timeout.InnerException);
        }

        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }
}
