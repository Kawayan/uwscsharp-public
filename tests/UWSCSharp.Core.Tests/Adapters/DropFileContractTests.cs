using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DropFileContractTests
{
    [Theory]
    [InlineData("DROPFILE()")]
    [InlineData("DROPFILE(Tick(),Tick())")]
    public async Task InvalidArityPrecedesEvaluation(string call)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PUBLIC calls=0\nTRY\n" + call
            + "\nEXCEPT\nPRINT calls\nENDTRY\nFUNCTION Tick()\n"
            + "calls=calls+1\nRESULT=1\nFEND", output.Add);
        Assert.Equal(["0"], output);
    }

    [Fact]
    public async Task TooManyArgumentsPrecedeEvaluation()
        => await InvalidArityPrecedesEvaluation("DROPFILE("
            + string.Join(',', Enumerable.Repeat("Tick()", 37)) + ")");

    [Theory]
    [InlineData(false, 34)]
    [InlineData(true, 32)]
    public async Task MaximumArityPreservesAllNames(
        bool withCoordinates, int fileCount)
    {
        string[] names = [.. Enumerable.Range(1, fileCount)
            .Select(index => $"file-{index}")];
        string prefix = withCoordinates ? "1,17,29" : "1";
        string arguments = string.Join(',', names.Select(name => $"\"{name}\""));
        var adapter = new Adapter();
        await new Interpreter(windows: adapter).RunAsync(
            $"DROPFILE({prefix},\"dir\",{arguments})", _ => { });

        DropFileRequest request = Assert.IsType<DropFileRequest>(adapter.Request);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, request.WindowId);
        Assert.Equal(withCoordinates ? 17 : (int?)null, request.X);
        Assert.Equal(withCoordinates ? 29 : (int?)null, request.Y);
        Assert.Equal("dir", request.Directory);
        Assert.Equal(names, request.Names);
    }

    [Fact]
    public async Task ExpressionsRunOnceFromLeftToRight()
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync("""
            PUBLIC calls=""
            DIM files[1]="first",TRUE
            DROPFILE(Tick(1),Tick(17),Tick(29),DirName(),files,LastName())
            PRINT calls
            FUNCTION Tick(number)
                calls=calls+number+"|"
                RESULT=number
            FEND
            FUNCTION DirName()
                calls=calls+"dir|"
                RESULT="directory"
            FEND
            FUNCTION LastName()
                calls=calls+"last|"
                RESULT="last"
            FEND
            """, output.Add);
        Assert.Equal(["1|17|29|dir|last|"], output);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(17, adapter.Request!.X);
        Assert.Equal(29, adapter.Request.Y);
        Assert.Equal(["first", "1", "last"], adapter.Request.Names);
    }

    [Theory]
    [InlineData("DROPFILE(0,\"dir\",\"file\")")]
    [InlineData("DROPFILE(-1,\"dir\",\"file\")")]
    [InlineData("DROPFILE(1,\"\",\"file\")")]
    [InlineData("DROPFILE(1,17,29,\"dir\")")]
    [InlineData("DROPFILE(1,17,\"29\",\"dir\",\"file\")")]
    [InlineData("DROPFILE(1,\"dir\",NULL)")]
    [InlineData("DROPFILE(1,\"dir\",\"\")")]
    [InlineData("DROPFILE(1,2147483648,0,\"dir\",\"file\")")]
    public async Task InvalidValuesDoNotReachAdapter(string source)
    {
        var adapter = new Adapter();
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: adapter).RunAsync(source, _ => { }));
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public void FlattensRowMajorAndNestedValuesWithoutEmptyNames()
    {
        var array = Value.FromArray(UwscArray.Create([1, 1],
            [Value.String("one"), Value.Empty, Value.Numeric(2), Value.String("three")]));
        var nested = Value.FromArray(UwscArray.Create([1], [array, Value.String("four")]));
        var request = DropFileRequest.Read([Value.Numeric(1), Value.String("dir"),
            nested, new Value(ValueKind.Omitted)]);
        Assert.Null(request.X);
        Assert.Null(request.Y);
        Assert.Equal(["one", "2", "three", "four"], request.Names);
    }

    [Fact]
    public void ExpandedNamesAcceptLimitAndRejectOneMore()
    {
        const int limit = UwscArray.MaximumElements;
        var first = Value.String("first");
        var last = Value.String("last");
        var names = Value.FromArray(UwscArray.Create([limit - 1],
            Enumerable.Repeat(first, limit - 1).Append(last)));
        var id = Value.Numeric(1);
        var directory = Value.String("dir");

        var request = DropFileRequest.Read([id, directory, names]);

        Assert.Equal(limit, request.Names.Count);
        Assert.Equal("first", request.Names[0]);
        Assert.Equal("last", request.Names[^1]);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() =>
                DropFileRequest.Read([id, directory, names, last]));
        Assert.Contains("ファイル名列の要素数が上限を超えています。",
            error.Message);
    }

    [Fact]
    public void RejectsNulAndEmptyArray()
    {
        Assert.Throws<InvalidOperationException>(() => DropFileRequest.Read(
            [Value.Numeric(1), Value.String("dir\0tail"), Value.String("one")]));
        Assert.Throws<InvalidOperationException>(() => DropFileRequest.Read(
            [Value.Numeric(1), Value.String("dir"), Value.String("one\0tail")]));
        Assert.Throws<InvalidOperationException>(() => DropFileRequest.Read(
            [Value.Numeric(1), Value.String("dir"), Value.FromArray(UwscArray.Create([-1]))]));
    }

    [Fact]
    public async Task BooleanCoordinatesUseExistingNumericValues()
    {
        var adapter = new Adapter();
        await new Interpreter(windows: adapter).RunAsync(
            "DROPFILE(1,TRUE,FALSE,\"dir\",TRUE,FALSE)", _ => { });
        Assert.Equal(1, adapter.Request!.X);
        Assert.Equal(0, adapter.Request.Y);
        Assert.Equal(["1", "0"], adapter.Request.Names);
    }


    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Calls
        {
            get; private set;
        }

        internal DropFileRequest? Request
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken)
        {
            Assert.Equal(BuiltinFunctionNames.DropFile, name);
            Request = DropFileRequest.Read(arguments);
            Calls++;
            return ValueTask.FromResult(Value.String(""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
