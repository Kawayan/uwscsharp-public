using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SpecialCharacterCompletionTests
{
    [Theory]
    [InlineData("<#CR>", "\r\n")]
    [InlineData("<#TAB>", "\t")]
    [InlineData("<#DBL>", "\"")]
    [InlineData("<#cr><#TaB><#dbl>", "\r\n\t\"")]
    [InlineData("a<#CR>b<#TAB>c<#DBL>d", "a\r\nb\tc\"d")]
    [InlineData("<<#TAB>>", "<\t>")]
    [InlineData("<#CR><#CR>", "\r\n\r\n")]
    [InlineData("<#UNKNOWN>", "<#UNKNOWN>")]
    [InlineData("<#CR", "<#CR")]
    [InlineData("<# CR>", "<# CR>")]
    [InlineData("<#TAB >", "<#TAB >")]
    [InlineData("<#LF>", "<#LF>")]
    public async Task LiteralExpansionAndPreservationAreExplicit(
        string literal, string expanded)
    {
        Assert.Equal([expanded], await Run("PRINT \"" + literal + "\""));
        Assert.Equal([literal], await Run("OPTION SPECIALCHAR\nPRINT \"" + literal + "\""));
    }

    [Theory]
    [InlineData("\"<#\"+\"TAB>\"", "<#TAB>")]
    [InlineData("CHR(60)+\"#CR>\"", "<#CR>")]
    [InlineData("REPLACE(\"[%TAB>]\",\"[%\",\"<#\")", "<#TAB>]")]
    public async Task RuntimeStringsAreNotExpandedAgain(string expression, string expected)
    {
        Assert.Equal([expected, expected], await Run("s=" + expression + "\nPRINT s\nPRINT F(s)"
            + "\nFUNCTION F(x)\nRESULT=x\nFEND"));
        Assert.Equal([expected, expected], await Run("OPTION SPECIALCHAR\ns="
            + expression + "\nPRINT s\nPRINT F(s)\nFUNCTION F(x)\nRESULT=x\nFEND"));
    }

    [Fact]
    public async Task ExpandedQuoteAndNewlineRemainStringData()
    {
        Assert.Equal(["\"//;_\r\nPRINT 99", "after"], await Run(
            "PRINT \"<#DBL>//;_<#CR>PRINT 99\"; PRINT \"after\" // <#DBL>"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallInheritsPreservationAndChildOptionDoesNotChangeParent(
        bool parent, bool child)
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-markers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                (child ? "OPTION SPECIALCHAR\n" : "") + "PRINT LENGTH(\"<#TAB>\")");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, (parent ? "OPTION SPECIALCHAR\n" : "")
                + "CALL child\nPRINT LENGTH(\"<#TAB>\")");
            var host = new TestHost();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
            Assert.Equal([parent || child ? "6" : "1", parent ? "6" : "1"], host.Output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedCallWithConflictingInheritedPolicyIsRejected(bool reverse)
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-marker-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "plain.uws"), "CALL shared");
            File.WriteAllText(Path.Combine(folder, "preserved.uws"), "OPTION SPECIALCHAR\nCALL shared");
            File.WriteAllText(Path.Combine(folder, "shared.uws"), "PRINT LENGTH(\"<#TAB>\")");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, reverse ? "CALL preserved\nCALL plain" : "CALL plain\nCALL preserved");
            ScriptException error = Assert.Throws<ScriptException>(() => ScriptCompiler.CompileFile(main));
            Assert.Contains("SPECIALCHAR", error.Message);
            Assert.Equal(reverse ? 1 : 2, error.Line);
            Assert.Equal(Path.Combine(folder, reverse ? "plain.uws" : "preserved.uws"),
                error.Data["UWSCSharp.SourcePath"]);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitSharedPolicyAllowsBothLoadOrders(bool reverse, bool includeRoutine)
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-marker-explicit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "plain.uws"), "CALL shared");
            File.WriteAllText(Path.Combine(folder, "preserved.uws"), "OPTION SPECIALCHAR\nCALL shared");
            File.WriteAllText(Path.Combine(folder, "shared.uws"),
                "OPTION SPECIALCHAR\nPRINT LENGTH(\"<#TAB>\")"
                + (includeRoutine ? "\nFUNCTION F()\nRESULT=1\nFEND" : ""));
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, reverse ? "CALL preserved\nCALL plain" : "CALL plain\nCALL preserved");
            var host = new TestHost();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
            Assert.Equal(["6", "6"], host.Output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePolicySharingAndCircularCompilationRemainSupported(bool preserve)
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-marker-cycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                "IF FALSE THEN CALL main\nPRINT LENGTH(\"<#TAB>\")");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, (preserve ? "OPTION SPECIALCHAR\n" : "")
                + "CALL child\nCALL child");
            var host = new TestHost();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
            string expected = preserve ? "6" : "1";
            Assert.Equal([expected, expected], host.Output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(false, "リターンの実行\r\n", "タブの実行\t")]
    [InlineData(true, "リターンの実行<#CR>", "タブの実行<#TAB>")]
    public async Task HelpSendStrTextReachesAdapterWithoutChangingOtherArguments(
        bool preserve, string carriageReturn, string tab)
    {
        var adapter = new RecordingTextAdapter();
        await new Interpreter(windows: adapter).RunAsync(
            (preserve ? "OPTION SPECIALCHAR\n" : "") + """
            メモ帳id=7
            SendStr(メモ帳id,"リターンの実行<#CR>",1,True)
            SendStr(メモ帳id,"タブの実行<#TAB>")
            """, _ => { });
        Assert.Equal([carriageReturn, tab], adapter.Text);
        Assert.Equal(Value.Numeric(1), adapter.Arguments[0][2]);
        Assert.Equal(Value.Numeric(1), adapter.Arguments[0][3]);
        Assert.Equal(2, adapter.Arguments[1].Count);
        Assert.Equal(1, adapter.Disposals);
    }

    private sealed class RecordingTextAdapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<string> Text
        {
            get;
        } = [];

        internal List<IReadOnlyList<Value>> Arguments
        {
            get;
        } = [];

        internal int Disposals
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            if (name == "GETSTR")
            {
                return ValueTask.FromResult(Value.String("<#TAB>"));
            }

            Assert.Equal("SENDSTR", name);
            Text.Add(arguments[1].Text);
            Arguments.Add(arguments);
            return ValueTask.FromResult(Value.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterTextRemainsDataWhenPrintedAndSentAgain(bool preserve)
    {
        var adapter = new RecordingTextAdapter();
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync(
            (preserve ? "OPTION SPECIALCHAR\n" : "")
            + "s=GETSTR(7)\nPRINT s\nSENDSTR(7,s)", output.Add);
        Assert.Equal(["<#TAB>"], output);
        Assert.Equal(["<#TAB>"], adapter.Text);
        Assert.Equal(1, adapter.Disposals);
    }
}
