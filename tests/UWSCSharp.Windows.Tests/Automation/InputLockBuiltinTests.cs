using System.Collections.Concurrent;
using System.ComponentModel;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class InputLockBuiltinTests
{
    [Theory]
    [InlineData("")]
    [InlineData("EXITEXIT(7)")]
    [InlineData("DIM failure=1/0")]
    public async Task InterpreterReleasesOwnershipOnEveryExit(string ending)
    {
        var backend = new Backend();
        var interpreter = new Interpreter(windows: Create(backend));
        Exception? error = await Record.ExceptionAsync(() => interpreter.RunAsync(
            "LOCKHARD(TRUE)\nLOCKHARD(TRUE)\n" + ending, _ => { }));
        if (ending.Contains("1/0", StringComparison.Ordinal))
        {
            Assert.IsType<ScriptException>(error);
        }
        else
        {
            Assert.Null(error);
        }

        Assert.Equal([true, false], [.. backend.Calls]);
        Assert.Single(backend.Threads.Distinct());
    }

    [Fact]
    public async Task CancellationReleasesOwnership()
    {
        var backend = new Backend();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: Create(backend)).RunAsync(
                "LOCKHARD(TRUE)\nPRINT 1\nSLEEP(60)",
                _ => cancellation.Cancel(), cancellation.Token));
        Assert.Equal([true, false], [.. backend.Calls]);
    }

    [Fact]
    public async Task OsFailureIsCatchableAndDoesNotClaimOwnership()
    {
        var backend = new Backend { FailBlock = true };
        List<string> output = [];
        await new Interpreter(windows: Create(backend)).RunAsync("""
            TRY
                LOCKHARD(TRUE)
            EXCEPT
                PRINT "caught"
            ENDTRY
            LOCKHARD(FALSE)
            """, output.Add);
        Assert.Equal(["caught"], output);
        Assert.Equal([true], [.. backend.Calls]);
    }

    [Fact]
    public async Task WorkerSessionExitDoesNotReleaseTheRun()
    {
        var backend = new Backend();
        await using IWindowsAutomationSession root = Create(backend).CreateSession();
        IWindowsAutomationSession child = root.Fork();
        await child.InvokeAsync(BuiltinFunctionNames.LockHard, [Value.Numeric(1)], default);
        await child.DisposeAsync();
        Assert.Equal([true], [.. backend.Calls]);
        await root.InvokeAsync(BuiltinFunctionNames.LockHard, [Value.Numeric(1)], default);
        Assert.Equal([true], [.. backend.Calls]);
        await root.InvokeAsync(BuiltinFunctionNames.LockHard, [], default);
        await root.DisposeAsync();
        Assert.Equal([true, false], [.. backend.Calls]);
    }

    private static WindowsAutomation Create(Backend backend) => new(
        _ => throw new InvalidOperationException("入力送信は想定していません。"),
        swappedMouseButtons: false, inputOwnershipFactory: () => backend);

    private sealed class Backend : IInputOwnershipBackend
    {
        internal bool FailBlock
        {
            get; init;
        }
        internal ConcurrentQueue<bool> Calls { get; } = new();
        internal ConcurrentQueue<int> Threads { get; } = new();

        public void SetBlocked(bool blocked)
        {
            Calls.Enqueue(blocked);
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (blocked && FailBlock)
            {
                throw new Win32Exception(5);
            }
        }

        public void Send(Native.Input input) => throw new NotSupportedException();
    }
}
