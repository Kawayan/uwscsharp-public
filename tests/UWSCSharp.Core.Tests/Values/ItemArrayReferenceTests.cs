using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ItemArrayReferenceTests
{
    private sealed class Windows : IWindowsAutomation, IWindowsAutomationSession
    {
        public IReadOnlyList<string> AllItems => ["first", "second"];

        internal bool Disposed
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Value.Numeric(2));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task CancelledVarBodyKeepsCallerSnapshotThroughFinally(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var windows = new Windows();
        var output = new List<string>();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: windows).RunAsync("""
                OPTION OPTFINALLY
                GETITEM(1,ITM_LIST)
                items=ALL_ITEM_LIST
                TRY
                    Change(items)
                FINALLY
                    PRINT items[0]
                    PRINT ALL_ITEM_LIST[0]
                ENDTRY
                """ + "\n" + kind + """
                 Change(VAR values[])
                    TRY
                        values[0]="changed"
                        PRINT "trigger"
                    FINALLY
                        PRINT values[0]
                    ENDTRY
                FEND
                """, text =>
            {
                output.Add(text);
                if (text == "trigger")
                {
                    cancellation.Cancel();
                }
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(["trigger", "changed", "first", "first"], output);
        Assert.True(windows.Disposed);
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task SuccessfulVarCopybackDoesNotModifyOriginalItemSnapshot(string kind)
    {
        var windows = new Windows();
        var output = new List<string>();
        await new Interpreter(windows: windows).RunAsync("""
            GETITEM(1,ITM_LIST)
            items=ALL_ITEM_LIST
            Change(items)
            PRINT items[0]
            PRINT ALL_ITEM_LIST[0]
            """ + "\n" + kind + """
             Change(VAR values[])
                values[0]="changed"
            FEND
            """, output.Add);
        Assert.Equal(["changed", "first"], output);
        Assert.True(windows.Disposed);
    }

    [Theory]
    [InlineData("PROCEDURE")]
    [InlineData("FUNCTION")]
    public async Task FailedVarBodyPreservesCallerAndSnapshotAfterFinally(string kind)
    {
        var windows = new Windows();
        var output = new List<string>();
        await new Interpreter(windows: windows).RunAsync("""
            GETITEM(1,ITM_LIST)
            items=ALL_ITEM_LIST
            TRY
                Change(items)
            EXCEPT
                PRINT items[0]
                PRINT ALL_ITEM_LIST[0]
            ENDTRY
            """ + "\n" + kind + """
             Change(VAR values[])
                TRY
                    values[0]="changed"
                    PRINT 1/0
                FINALLY
                    PRINT values[0]
                ENDTRY
            FEND
            """, output.Add);
        Assert.Equal(["changed", "first", "first"], output);
        Assert.True(windows.Disposed);
    }
}
