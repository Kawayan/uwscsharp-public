using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ImageResultInitialTests
{
    private sealed class Failure : IWindowsAutomation, IWindowsAutomationSession
    {
        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            throw new InvalidOperationException("画像を取得できません。");

        public string? GetScriptErrorMessage(string name, Exception exception) =>
            "画像を取得できません。";
    }

    [Fact]
    public async Task FirstFailurePreservesDefinedEmptyResults()
    {
        var output = new List<string>();
        await new Interpreter(windows: new Failure()).RunAsync("""
            PRINT G_IMG_X
            PRINT G_IMG_Y
            PRINT LENGTH(ALL_IMG_X)
            PRINT LENGTH(ALL_IMG_Y)
            TRY
                CHKIMG("missing.bmp")
            EXCEPT
                PRINT "failed"
            ENDTRY
            PRINT G_IMG_X
            PRINT G_IMG_Y
            PRINT JOIN(ALL_IMG_X,",")
            PRINT JOIN(ALL_IMG_Y,",")
            """, output.Add);
        Assert.Equal(["-999999", "-999999", "0", "0", "failed",
            "-999999", "-999999", "", ""], output);
    }

    [Fact]
    public async Task InitialArraysAreIndependentAcrossVariablesAndRuns()
    {
        var runner = new Interpreter();
        var output = new List<string>();
        await runner.RunAsync("""
            RESIZE(ALL_IMG_X,0)
            ALL_IMG_X[0]=7
            PRINT LENGTH(ALL_IMG_Y)
            """, output.Add);
        await runner.RunAsync("""
            PRINT G_IMG_X
            PRINT LENGTH(ALL_IMG_X)
            PRINT LENGTH(ALL_IMG_Y)
            """, output.Add);
        Assert.Equal(["0", "-999999", "0", "0"], output);
    }

    [Fact]
    public async Task ExplicitGlobalDeclarationsKeepTheirInitializers()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            PUBLIC G_IMG_X=12
            PUBLIC ALL_IMG_X=SPLIT("a,b",",")
            PRINT G_IMG_X
            PRINT JOIN(ALL_IMG_X,",")
            PRINT G_IMG_Y
            PRINT LENGTH(ALL_IMG_Y)
            """, output.Add);
        Assert.Equal(["12", "a,b", "-999999", "0"], output);
    }
}
