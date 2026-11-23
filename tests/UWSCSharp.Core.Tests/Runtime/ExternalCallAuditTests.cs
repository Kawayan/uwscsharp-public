using System.Text;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests
{
    public static IEnumerable<object[]> CallOptionPairs()
    {
        for (int parent = 0; parent < 16; parent++)
        {
            for (int child = 0; child < 16; child++)
            {
                yield return [parent, child];
            }
        }
    }

    private static string CallOptions(int mask)
    {
        string[] names = ["OPTFINALLY", "EXPLICIT", "SAMESTR", "SPECIALCHAR"];
        return string.Concat(names.Select((name, bit) => (mask & (1 << bit)) != 0
            ? "OPTION " + name + "\n" : ""));
    }

    [Theory]
    [MemberData(nameof(CallOptionPairs))]
    public async Task ParentAndChildOptionMatrixPreservesScopeAndCancellation(int parent,
        int child)
    {
        Write("child.uws", CallOptions(child) + """
            PRINT "child:"+LENGTH("<#TAB>")
            TRY
                PRINT "cancel"
            FINALLY
                PRINT "cleanup"
            ENDTRY
            """);
        string main = Write("main.uws", CallOptions(parent) + """
            PRINT "cmp:"+("A"="a")
            PRINT "parent:"+LENGTH("<#TAB>")
            TRY
                missing=1
                PRINT "loose"
            EXCEPT
                PRINT "strict"
            ENDTRY
            CALL child
            """);
        using var cancellation = new CancellationTokenSource();
        List<string> output = [];
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(text =>
                {
                    output.Add(text);
                    if (text == "cancel")
                    {
                        cancellation.Cancel();
                    }
                }), cancellation.Token));
        int combined = parent | child;
        List<string> expected =
        [
            (combined & 4) != 0 ? "cmp:0" : "cmp:1",
            (parent & 8) != 0 ? "parent:6" : "parent:1",
            (combined & 2) != 0 ? "strict" : "loose",
            (combined & 8) != 0 ? "child:6" : "child:1",
            "cancel"
        ];
        if ((combined & 1) != 0)
        {
            expected.Add("cleanup");
        }

        Assert.Equal(expected, output);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task UnexecutedGrandchildContributesGlobalOptionsButNotParentLiterals()
    {
        Write("child.uws", "IF FALSE THEN CALL leaf");
        Write("leaf.uws", CallOptions(15) + "CONST leafLength=LENGTH(\"<#TAB>\")");
        string main = Write("main.uws", """
            IF FALSE THEN CALL child
            PRINT "A"="a"
            PRINT LENGTH("<#TAB>")
            PRINT leafLength
            TRY
                missing=1
            EXCEPT
                PRINT "strict"
            ENDTRY
            TRY
                PRINT "cancel"
            FINALLY
                PRINT "cleanup"
            ENDTRY
            """);
        using var cancellation = new CancellationTokenSource();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(text =>
                {
                    output.Add(text);
                    if (text == "cancel")
                    {
                        cancellation.Cancel();
                    }
                }), cancellation.Token));
        Assert.Equal(["0", "1", "6", "strict", "cancel", "cleanup"], output);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("cp932")]
    public async Task ImportedJapaneseSourceUsesSupportedDecoders(string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(false),
            "utf8-bom" => new UTF8Encoding(true),
            _ => Encoding.GetEncoding(932)
        };
        File.WriteAllText(Path.Combine(folder, "子 file.uws"), "PRINT \"日本語\"", encoding);
        string main = Write("main.uws", "CALL \"子 file.uws\"");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["日本語"], output);
    }

    [Fact]
    public async Task ExplicitExtensionDoesNotAppendUwsWhenBothCandidatesExist()
    {
        Write("child.txt", "PRINT \"explicit\"");
        Write("child.txt.uws", "PRINT \"appended\"");
        string main = Write("main.uws", "CALL child.txt");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["explicit"], output);
    }

    [Fact]
    public async Task ParenthesesInsideQuotedNameAreNotReparsedAsArguments()
    {
        Write("child file.uws", "PRINT \"split\"\nPRINT LENGTH(PARAM_STR)");
        Write("child file.uws(7)", "PRINT \"literal\"\nPRINT LENGTH(PARAM_STR)");
        string main = Write("main.uws", "CALL \"child file.uws(7)\"");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["literal", "0"], output);
    }

    [Fact]
    public async Task FixedNameDoesNotEvaluateSameNamedVariable()
    {
        Write("target.uws", "PRINT \"literal\"");
        Write("actual.uws", "PRINT \"evaluated\"");
        string main = Write("main.uws", "DIM target=\"actual\"\nCALL target");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["literal"], output);
    }

    [Fact]
    public async Task VirtualMainSourceUsesExplicitSourcePathForStaticImports()
    {
        Write("child.uws", "PRINT \"child\"");
        string main = Path.Combine(folder, "unsaved.uws");
        Assert.False(File.Exists(main));
        List<string> output = [];
        await new Interpreter().RunAsync("CALL child", output.Add, default,
            new ScriptRunOptions([]) { SourcePath = main });
        Assert.Equal(["child"], output);
        Assert.False(File.Exists(main));
    }

    [Fact]
    public async Task HostCanReenterSameRunnerWithoutReplacingSuspendedCallArguments()
    {
        Write("child.uws", "PRINT \"trigger\"\nPRINT PARAM_STR[0]");
        string main = Write("main.uws", "CALL child(PARAM_STR[0]+\"-child\")\nPRINT PARAM_STR[0]");
        ScriptProgram program = ScriptCompiler.CompileFile(main);
        var interpreter = new Interpreter();
        var host = new ReentrantCallHost(interpreter, program);
        await interpreter.RunAsync(program, host, default,
            new ScriptRunOptions(["outer-root"]));
        Assert.Equal(["trigger", "outer-root-child", "outer-root"], host.Outer);
        Assert.Equal(["trigger", "inner-root-child", "inner-root"], host.Inner);
    }

    private sealed class ReentrantCallHost(Interpreter runner, ScriptProgram program) : IScriptHost
    {
        internal List<string> Outer { get; } = [];

        internal List<string> Inner { get; } = [];

        public async ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Outer.Add(text);
            if (text == "trigger")
            {
                await runner.RunAsync(program, new CallbackScriptHost(Inner.Add), token,
                    new ScriptRunOptions(["inner-root"]));
            }
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public async Task RoutineAndCallShareTheDepthLimit(int depth, bool accepted)
    {
        Write("child.uws", """
            visits=visits+1
            IFB VAL(PARAM_STR[0])>1
                CALL child(VAL(PARAM_STR[0])-1)
            ENDIF
            """);
        string main = Write("main.uws", "PUBLIC visits=0\nTRY\nF()\nEXCEPT\n"
            + "PRINT \"caught\"\nENDTRY\nPRINT visits\nPROCEDURE F()\n"
            + "CALL child(" + depth + ")\nFEND");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(accepted ? ["127"] : new[] { "caught", "127" }, output);
    }
}
