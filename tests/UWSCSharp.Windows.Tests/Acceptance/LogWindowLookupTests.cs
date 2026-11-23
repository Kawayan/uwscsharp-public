using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class LogWindowLookupTests
{
    [Fact]
    public async Task LookupRegistersSuppliedWindowAndKeepsIdStable()
    {
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        int calls = 0;
        var automation = WindowsAutomation.WithLogWindow(token =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return ValueTask.FromResult(fixture.Root);
        });
        var output = new List<string>();
        await new Interpreter(windows: automation).RunAsync("""
            DIM id=GETID(GET_LOGPRINT_WIN)
            PRINT id>0
            PRINT GETID(GET_LOGPRINT_WIN,, -1)=id
            PRINT IDTOHND(id)
            """, output.Add);
        Assert.Equal(["1", "1", ((long)fixture.Root).ToString()], output);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingWindowReturnsNotFound(bool hasProvider)
    {
        WindowsAutomation automation = hasProvider
            ? WindowsAutomation.WithLogWindow(_ => ValueTask.FromResult((nint)0)) : new WindowsAutomation();
        var output = new List<string>();
        await new Interpreter(windows: automation).RunAsync(
            "PRINT GETID(GET_LOGPRINT_WIN,, -1)", output.Add);
        Assert.Equal(["-1"], output);
    }

    [Fact]
    public async Task CancellationAfterProviderKeepsToken()
    {
        using var cancellation = new CancellationTokenSource();
        var automation = WindowsAutomation.WithLogWindow(_ =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult((nint)123);
        });
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "GETID(GET_LOGPRINT_WIN)", _ => { }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ProviderFailureBypassesScriptCatch()
    {
        var failure = new IOException("ログウィンドウの取得失敗");
        var automation = WindowsAutomation.WithLogWindow(_ => ValueTask.FromException<nint>(failure));
        var output = new List<string>();
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter(windows: automation).RunAsync("""
                TRY
                    GETID(GET_LOGPRINT_WIN)
                EXCEPT
                    PRINT "caught"
                ENDTRY
                """, output.Add));
        Assert.Same(failure, error);
        Assert.Empty(output);
    }

    [Fact]
    public async Task InvalidArgumentsDoNotCallProvider()
    {
        bool called = false;
        var automation = WindowsAutomation.WithLogWindow(_ =>
        {
            called = true;
            return ValueTask.FromResult((nint)0);
        });
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "GETID(GET_LOGPRINT_WIN,, -2)", _ => { }));
        Assert.False(called);
    }
}
