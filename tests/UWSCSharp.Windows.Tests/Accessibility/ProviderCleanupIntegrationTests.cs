using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ProviderCleanupIntegrationTests
{
    private sealed class Operations : ProviderCleanupOperations
    {
        internal readonly ConcurrentQueue<Exception> Failures = new();

        internal override void Release(object value)
        {
            base.Release(value);
            Fail("COM参照の解放後に注入");
        }

        internal override void ReleasePointer(nint value)
        {
            base.ReleasePointer(value);
            Fail("IUnknown参照の解放後に注入");
        }

        private void Fail(string message)
        {
            var failure = new InvalidOperationException(message);
            Failures.Enqueue(failure);
            throw failure;
        }
    }

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object accessible);

    [Theory]
    [InlineData("script")]
    [InlineData("callback")]
    [InlineData("cancel")]
    public void ActualMsaaTraversalPreservesCallbackOrCancellation(string kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "script" => new ScriptException(7, "callbackのscript失敗"),
            "cancel" => new OperationCanceledException(cancel.Token),
            _ => new InvalidOperationException("通知callback失敗")
        };
        var provider = new AccessibleTestProvider(42, 0, "", "value");
        var operations = new Operations();
        using IDisposable scope = ProviderCleanupOperations.UseForTesting(operations);
        Exception actual = Assert.ThrowsAny<Exception>(() =>
            AccessibleItems.ReadProvider(provider, 16777216, false, true, 0,
                cancel.Token, onItem: _ => throw primary));
        Assert.Same(primary, actual);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            actual.Data[ProviderCleanup.DataKey]);
        Assert.NotEmpty(cleanup.InnerExceptions);
        Assert.Equal(operations.Failures.ToArray(), cleanup.InnerExceptions);
        if (actual is OperationCanceledException cancelled)
        {
            Assert.Equal(cancel.Token, cancelled.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualWindowCleanupKeepsOperationOrRemainsHostFailure(bool bodyFails)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new AccessibleTestProvider(42, 0, "", "good");
        if (bodyFails)
        {
            provider.ReadingValue = () => throw new COMException(
                "操作失敗", unchecked((int)0x80004005));
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        var operations = new Operations();
        using IDisposable scope = ProviderCleanupOperations.UseForTesting(operations);
        var runner = new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")));
        List<string> output = [];
        string call = "GETITEM(id,16777728)\n";
        string source = $"id=HNDTOID({(long)fixture.Root})\nTRY\n"
            + (bodyFails ? call : "TRY\n" + call
                + "EXCEPT\nPRINT \"caught\"\nENDTRY\n")
            + "FINALLY\nPRINT \"finally\"\nENDTRY\nPRINT \"after\"";
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            runner.RunAsync(source, output.Add));
        Assert.Equal(["finally"], output);
        Assert.NotEmpty(operations.Failures);
        if (bodyFails)
        {
            ScriptException script = Assert.IsType<ScriptException>(failure);
            Assert.Equal(3, script.Line);
            Exception? original = script.InnerException?.InnerException;
            Assert.NotNull(original);
            Assert.Equal(unchecked((int)0x80004005), original.HResult);
            AggregateException cleanup = Assert.IsType<AggregateException>(
                original.Data[ProviderCleanup.DataKey]);
            Assert.Equal(operations.Failures.ToArray(), cleanup.InnerExceptions);
        }
        else
        {
            Assert.IsType<AggregateException>(failure);
            Assert.False(WindowsOperationErrors.Contains(failure));
        }
    }
}
