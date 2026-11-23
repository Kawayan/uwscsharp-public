using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowEnumerationContractTests
{
    [Fact]
    public async Task PublishesSnapshotAndEnumeratesInCallerScope()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            PRINT GETALLWIN()
            saved = ALL_WIN_ID
            FOR id IN GETALLWIN(7)
                PRINT id
            NEXT
            PRINT LENGTH(ALL_WIN_ID)
            PRINT saved[0]
            PRINT saved[1]
            """, output.Add);
        Assert.Equal(["2", "9", "1", "3", "4"], output);
        Assert.Equal(2, adapter.Calls);
        Assert.True(adapter.Disposed);
    }

    [Theory]
    [InlineData("GETALLWIN(1, side())")]
    [InlineData("MUSCUR(side())")]
    public async Task RejectsArityBeforeEvaluatingArguments(string expression)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync($"""
            TRY
                PRINT {expression}
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION side()
                PRINT "side"
                RESULT = 1
            FEND
            """, output.Add);
        Assert.Equal(["caught"], output);
        Assert.Equal(0, adapter.Calls);
    }

    private sealed class Adapter : IWindowsAutomation,
        IWindowsAutomationSession
    {
        public int Calls
        {
            get; private set;
        }

        public bool Disposed
        {
            get; private set;
        }

        public IReadOnlyList<int> AllWindowIds { get; private set; } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            AllWindowIds = arguments.Count == 0 ? [3, 4] : [9];
            return ValueTask.FromResult(Value.Numeric(AllWindowIds.Count));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
