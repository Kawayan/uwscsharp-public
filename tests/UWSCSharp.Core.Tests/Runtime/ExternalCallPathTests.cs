using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed partial class ExternalCallTests
{
    [Theory]
    [InlineData("\"\"")]
    [InlineData("''")]
    [InlineData("\"child.uws\" \"other\"")]
    [InlineData("child\"suffix\".uws")]
    [InlineData("'child.uws")]
    [InlineData("\"child.uws")]
    public void InvalidQuotedTargetIsRejectedWithCallerLine(string target)
    {
        string main = Write("main.uws", "\nCALL " + target);
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.CompileFile(main));
        Assert.Equal(2, error.Line);
        Assert.Equal(main, error.Data["UWSCSharp.SourcePath"]);
    }

    [Theory]
    [InlineData("child.uws", "child.uws")]
    [InlineData("\"child file.uws\"", "child file.uws")]
    [InlineData("'child.uws'", "child.uws")]
    [InlineData("\"child(test).uws\"", "child(test).uws")]
    [InlineData("\"child;test.uws\"", "child;test.uws")]
    [InlineData("child's.uws", "child's.uws")]
    [InlineData("\"'child.uws\"", "'child.uws")]
    public async Task ValidFixedNamesKeepTheirTextAndArgumentOrder(string target,
        string name)
    {
        Write(name, "PRINT LENGTH(PARAM_STR)\nPRINT PARAM_STR[0]\nPRINT PARAM_STR[1]");
        string main = Write("main.uws", "CALL " + target + "(\"a,b\",ABS(-2))");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(["2", "a,b", "2"], output);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task FileLimitCountsRootAndDistinctNormalizedPaths(int count,
        bool accepted)
    {
        for (int i = 1; i < count; i++)
        {
            Write($"child{i}.uws", "PRINT " + i);
        }

        string main = Write("main.uws", string.Join("\n",
            Enumerable.Range(1, count - 1).Select(i => $"CALL child{i}"))
            + "\nCALL .\\child1.uws");
        if (!accepted)
        {
            ScriptException error = Assert.Throws<ScriptException>(() =>
                ScriptCompiler.CompileFile(main));
            Assert.Equal(128, error.Line);
            Assert.Equal(main, error.Data["UWSCSharp.SourcePath"]);
            Assert.Contains("読込ファイル数が上限", error.Message);
            return;
        }

        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(Enumerable.Range(1, count - 1).Select(i => i.ToString())
            .Append("1"), output);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task RecursiveCallDepthAcceptsLimitAndRejectsNextLevel(int depth,
        bool accepted)
    {
        Write("child.uws", """
            visits=visits+1
            IFB VAL(PARAM_STR[0])>1
                CALL child(VAL(PARAM_STR[0])-1)
            ENDIF
            """);
        string main = Write("main.uws", "PUBLIC visits=0\nTRY\nCALL child("
            + depth + ")\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT visits");
        List<string> output = [];
        await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
            new CallbackScriptHost(output.Add));
        Assert.Equal(accepted ? ["128"] : new[] { "caught", "128" }, output);
    }
}
