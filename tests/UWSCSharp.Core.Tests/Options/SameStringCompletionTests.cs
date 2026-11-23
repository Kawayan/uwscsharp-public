using System.Globalization;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SameStringCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllRelationalOperatorsUseOneComparisonPolicy(bool sensitive)
    {
        string[] expected = sensitive ? ["0", "1", "1", "0", "1", "0"]
            : ["1", "0", "0", "0", "1", "1"];
        Assert.Equal(expected, await Run(Option(sensitive) + """
            PRINT "A"="a"
            PRINT "A"<>"a"
            PRINT "A"<"a"
            PRINT "A">"a"
            PRINT "A"<="a"
            PRINT "A">="a"
            """));
    }

    [Theory]
    [InlineData("A", "a", "1")]
    [InlineData("é", "É", "1")]
    [InlineData("я", "Я", "1")]
    [InlineData("σ", "Σ", "1")]
    [InlineData("é", "é", "0")]
    [InlineData("Ａ", "A", "0")]
    [InlineData("あ", "ア", "0")]
    [InlineData("ß", "SS", "0")]
    public async Task UnicodeCaseFoldingDoesNotNormalizeOrExpandText(
        string left, string right, string insensitive)
    {
        string expression = "PRINT \"" + left + "\"=\"" + right + "\"";
        Assert.Equal([insensitive], await Run(expression));
        Assert.Equal(["0"], await Run(Option(true) + expression));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchReplaceAndBetweenShareCasePolicy(bool sensitive)
    {
        string[] expected = sensitive
            ? ["2", "4", "2", "AxAx", "AxAx", "two", "", ""]
            : ["1", "2", "3", "xxxx", "xxxx", "one", "two", "one"];
        Assert.Equal(expected, await Run(Option(sensitive) + """
            PRINT POS("a","AaAa")
            PRINT POS("a","AaAa",2)
            PRINT POS("a","AaAa",-2)
            PRINT REPLACE("AaAa","a","x")
            PRINT CHGMOJ("AaAa","a","x")
            PRINT BETWEENSTR("<A>one</A><a>two</a>","<a>","</a>")
            PRINT BETWEENSTR("<A>one</A><a>two</a>","<a>","</a>",2)
            PRINT BETWEENSTR("<A>one</A><a>two</a>","<a>","</a>",-2)
            """));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnicodeSearchUsesSamePolicyAsSelect(bool sensitive)
    {
        Assert.Equal(sensitive ? ["2", "Éx", "", "2"] : ["1", "xx", "one", "1"],
            await Run(Option(sensitive) + """
                PRINT POS("é","Éé")
                PRINT REPLACE("Éé","é","x")
                PRINT BETWEENSTR("Éoneé","é","é")
                SELECT "É"
                CASE "é"
                    PRINT 1
                DEFAULT
                    PRINT 2
                SELEND
                """));
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public async Task ComparisonAndSearchDoNotUseCurrentCulture(string name)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            Assert.Equal(["1", "1"], await Run("PRINT \"I\"=\"i\"\nPRINT POS(\"i\",\"Ii\")"));
            Assert.Equal(["0", "2"], await Run("OPTION SAMESTR\nPRINT \"I\"=\"i\"\nPRINT POS(\"i\",\"Ii\")"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentifierMarkerTokenSplitSortAndHashPoliciesRemainIndependent(bool sensitive)
    {
        Assert.Equal(["7", "1", "a|b", "a|b", "a,B", "1", "0"],
            await Run(Option(sensitive) + """
                DIM value=7
                PRINT VALUE
                PRINT LENGTH("<#tab>")
                DIM text="aAb"
                PRINT TOKEN("A",text)+"|"+text
                PRINT JOIN(SPLIT("aAb","A",FALSE),"|")
                DIM a[]="a","B"
                QSORT(a)
                PRINT JOIN(a,",")
                HASHTBL h
                h["A"]=1
                PRINT h["a",HASH_EXISTS]
                HASHTBL strict=HASH_CASECARE
                strict["A"]=1
                PRINT strict["a",HASH_EXISTS]
                """));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericConversionAndNulRemainSeparateFromCasePolicy(bool sensitive)
    {
        Assert.Equal(["1", "0", "0"], await Run(Option(sensitive) + """
            PRINT 2="02"
            PRINT "2"="02"
            PRINT ("a"+CHR(0)+"b")=("a"+CHR(0)+"c")
            """));
        await Assert.ThrowsAsync<ScriptException>(() => Run(Option(sensitive) + "PRINT \"bad\"=2"));
        await Assert.ThrowsAsync<ScriptException>(() => Run(Option(sensitive) + "PRINT NULL=NULL"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObjectIdentityDoesNotBecomeTextComparison(bool sensitive)
    {
        var adapter = new IdentityAdapter();
        var output = new List<string>();
        await new Interpreter(windows: adapter).RunAsync(Option(sensitive)
            + "DIM o=CREATEOLEOBJ(\"fixture\")\nPRINT o=o\nPRINT o=o.Other\nPRINT o=NOTHING", output.Add);
        Assert.Equal(["1", "0", "0"], output);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnyLoadedOptionAffectsParentAndChild(bool parent, bool child)
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-samestr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"), Option(child) + "PRINT \"A\"=\"a\"");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, Option(parent) + "PRINT \"A\"=\"a\"\nCALL child");
            var host = new TestHost();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
            string expected = parent || child ? "0" : "1";
            Assert.Equal([expected, expected], host.Output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task UnexecutedCallOptionStillAffectsParent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "uwsc-samestr-unused-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "child.uws"), "OPTION SAMESTR");
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main, "IF FALSE THEN CALL child\nPRINT \"A\"=\"a\"");
            var host = new TestHost();
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main), host);
            Assert.Equal(["0"], host.Output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ParallelProgramsAndPureApiDoNotShareOptionState()
    {
        var interpreter = new Interpreter();
        var sensitive = new List<string>();
        var insensitive = new List<string>();
        await Task.WhenAll(interpreter.RunAsync(Option(true) + "PRINT \"A\"=\"a\"", sensitive.Add),
            interpreter.RunAsync("PRINT \"A\"=\"a\"", insensitive.Add));
        Assert.Equal(["0"], sensitive);
        Assert.Equal(["1"], insensitive);
        Assert.Equal(1, ExpressionEvaluator.Evaluate("\"A\"=\"a\"").Number);
    }

    [Fact]
    public async Task OptionRemainsEffectiveInFinallyAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("OPTION SAMESTR,OPTFINALLY\nTRY\nPRINT \"trigger\"\n"
                + "FINALLY\nPRINT \"A\"=\"a\"\nENDTRY", text =>
                {
                    output.Add(text);
                    cancellation.Cancel();
                }, cancellation.Token));
        Assert.Equal(["trigger", "0"], output);
    }

    [Theory]
    [InlineData("OPTION SAMESTR,")]
    [InlineData("OPTION SAMESTR,UNKNOWN")]
    [InlineData("PRINT 1\nOPTION SAMESTR")]
    [InlineData("OPTION SAMESTR=FALSE")]
    public void InvalidOptionSyntaxIsRejected(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Fact]
    public async Task DuplicateOptionIsIdempotent()
    {
        Assert.Equal(["0"], await Run("OPTION SAMESTR\nOPTION SAMESTR,SAMESTR\nPRINT \"A\"=\"a\""));
    }

    private static string Option(bool sensitive) => sensitive ? "OPTION SAMESTR\n" : "";

    private sealed class IdentityAdapter : IWindowsAutomation, IWindowsAutomationSession
    {
        private readonly object other = new();

        internal int Disposals
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token)
        {
            Assert.Equal("Other", name);
            return ValueTask.FromResult(Value.FromObject(other));
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
