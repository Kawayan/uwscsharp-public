using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class TokenContractTests
{
    [Theory]
    [InlineData(",;", "a;b,c", false, false, "a", "b,c")]
    [InlineData(",", ",a", false, false, "", "a")]
    [InlineData(",", ",,a,,b", true, false, "a", "b")]
    [InlineData(",", "a,", false, false, "a", "")]
    [InlineData(",", "a,,,", true, false, "a", "")]
    [InlineData(",", "\"a,b\",c", false, true, "\"a,b\"", "c")]
    [InlineData(",", "\"a,b\",c", false, false, "\"a", "b\",c")]
    [InlineData(",", "a\"b,c\"d,e", false, true, "a\"b,c\"d", "e")]
    [InlineData(",", "\"a\"\"b,c\",d", false, true, "\"a\"\"b,c\"", "d")]
    [InlineData("\"", "a\"b", false, false, "a", "b")]
    [InlineData("😀", "a😁b😀c", false, false, "a😁b", "c")]
    [InlineData("😀", "😀😀a😀😀b", true, false, "a", "b")]
    [InlineData("\0", "a\0b", false, false, "a", "b")]
    public void DelimiterSetQuotesAndRemainderHaveExplicitRules(string separators,
        string source, bool skip, bool quotes, string expected, string rest)
    {
        (string Token, Value Remainder) result = StringOperations.Token(separators, source, skip, quotes);
        Assert.Equal(expected, result.Token);
        Assert.Equal(ValueKind.String, result.Remainder.Kind);
        Assert.Equal(rest, result.Remainder.Text);
    }

    [Theory]
    [InlineData(",", "", false, false, "")]
    [InlineData(",", ",,,", true, false, "")]
    [InlineData("", "a,b", false, false, "a,b")]
    [InlineData(",", "last", false, false, "last")]
    [InlineData(",", "\"a,b", false, true, "\"a,b")]
    [InlineData("\"", "\"a,b\"", true, true, "\"a,b\"")]
    [InlineData("a", "A", false, false, "A")]
    public void NoDelimiterProducesEmptyValueInsteadOfEmptyString(string separators,
        string source, bool skip, bool quotes, string expected)
    {
        (string Token, Value Remainder) result = StringOperations.Token(separators, source, skip, quotes);
        Assert.Equal(expected, result.Token);
        Assert.Equal(ValueKind.Empty, result.Remainder.Kind);
    }

    [Fact]
    public void IsolatedSurrogatesArePreservedAndDoNotMatchInsidePairs()
    {
        Assert.Equal(("a😀b", Value.Empty), StringOperations.Token("\ud83d", "a😀b"));
        Assert.Equal(("a😀b", Value.Empty), StringOperations.Token("\ude00", "a😀b"));
        Assert.Equal(("a\ud83db", Value.Empty), StringOperations.Token("😀", "a\ud83db"));
        Assert.Equal(("a", Value.String("b")), StringOperations.Token("\ud83d", "a\ud83db"));
        Assert.Equal(("a", Value.String("b")), StringOperations.Token("\ude00", "a\ude00b"));
    }

    [Theory]
    [InlineData("1e20", "a", "b")]
    [InlineData("\"2147483648\"", "a", "b")]
    [InlineData("\" 1e0 \"", "a", "b")]
    [InlineData("0.9", "", ",a,,b")]
    [InlineData("-0.9", "", ",a,,b")]
    [InlineData("EMPTY", "", ",a,,b")]
    [InlineData("-1", "a", "b")]
    public async Task FiniteFlagsTruncateWithoutAnInt32Limit(string flag, string expected, string rest)
    {
        Assert.Equal([expected, rest], await Run(
            "s=\",,a,,b\"\nPRINT TOKEN(\",\",s," + flag + ")\nPRINT s"));
    }

    [Theory]
    [InlineData("\"$1\"")]
    [InlineData("\"NaN\"")]
    [InlineData("NULL")]
    [InlineData("\"\"")]
    public async Task BadFlagsDoNotPublishRemainder(string flag)
    {
        Assert.Equal(["kept", "a,b"], await Run(
            "s=\"a,b\"\nr=\"kept\"\nTRY\nr=TOKEN(\",\",s," + flag
            + ")\nEXCEPT\nPRINT r\nENDTRY\nPRINT s"));
    }

    [Theory]
    [InlineData("TOKEN()")]
    [InlineData("TOKEN(,s)")]
    [InlineData("TOKEN(Tick(),)")]
    [InlineData("TOKEN(Tick(),Tick())")]
    [InlineData("TOKEN(\",\",s,Tick(),Tick(),Tick())")]
    public async Task InvalidCallsPreventAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0", "a,b"], await Run(
            "PUBLIC calls=0\ns=\"a,b\"\nTRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nPRINT s\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ArgumentsRunInOrderAndOnlyTheFrozenElementIsReplaced()
    {
        Assert.Equal(["delimiter", "index", "skip", "quotes", "a", "1", "b|changed"], await Run("""
            PUBLIC values
            PUBLIC calls=0
            DIM initial[]="a,b","other"
            values=initial
            PRINT TOKEN(Delimiter(),values[Index()],Flag("skip"),Flag("quotes"))
            PRINT calls
            PRINT JOIN(values,"|")
            FUNCTION Delimiter()
                PRINT "delimiter"
                RESULT=","
            FEND
            FUNCTION Index()
                PRINT "index"
                calls=calls+1
                RESULT=calls-1
            FEND
            FUNCTION Flag(name)
                PRINT name
                values[1]="changed"
                RESULT=0
            FEND
            """));
    }

    [Fact]
    public async Task HashKeyAndVarParameterUseTheSharedReferenceRules()
    {
        Assert.Equal(["a", "1", "b", "c", "d"], await Run("""
            PUBLIC calls=0
            HASHTBL h
            h[""]="a,b"
            PRINT TOKEN(",",h[Key()])
            PRINT calls
            PRINT h[""]
            s="c,d"
            Take(s)
            PRINT s
            FUNCTION Key()
                calls=calls+1
                RESULT=""
            FEND
            PROCEDURE Take(VAR value)
                PRINT TOKEN(",",value)
            FEND
            """));
    }

    [Fact]
    public async Task DefaultsNumericStringsSameStringAndEndStatesRemainDistinct()
    {
        Assert.Equal(["a", "", "", "1", "3", "A,a"], await Run("""
            OPTION SAMESTR
            s="a,"
            PRINT TOKEN(",",s,,)
            PRINT s
            PRINT TOKEN(",",s)
            s=123
            PRINT TOKEN(2,s)
            PRINT s
            s="A,a"
            PRINT TOKEN("x",s)
            """));
    }

    [Fact]
    public async Task ConversionFailureKeepsReferenceButPreservesCompletedEffects()
    {
        Assert.Equal(["1", "a,b"], await Run("""
            PUBLIC calls=0
            s="a,b"
            TRY
                TOKEN(NULL,s,Tick())
            EXCEPT
                PRINT calls
            ENDTRY
            PRINT s
            FUNCTION Tick()
                calls=calls+1
                RESULT=0
            FEND
            """));
        Assert.Equal(["0"], await Run("""
            PUBLIC calls=0
            CONST s="a,b"
            TRY
                TOKEN(Tick(),s)
            EXCEPT
                PRINT calls
            ENDTRY
            FUNCTION Tick()
                calls=calls+1
                RESULT=","
            FEND
            """));
    }

    [Fact]
    public async Task ParentChangeDuringFlagsFailsWithoutRestoringArgumentEffects()
    {
        Assert.Equal(["caught", "7"], await Run("""
            PUBLIC a
            DIM initial[]="a,b","other"
            a=initial
            TRY
                TOKEN(",",a[0],ChangeParent())
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT a
            FUNCTION ChangeParent()
                a=7
                RESULT=0
            FEND
            """));
    }

    [Fact]
    public void RepeatedDelimiterSetsDoNotAllocatePerInputCharacter()
    {
        string separators = new(',', 1_000_000);
        string source = new string('a', 1_000_000) + ",tail";
        StringOperations.Token(",", "warm,up");
        long before = GC.GetAllocatedBytesForCurrentThread();
        (string Token, Value Remainder) result = StringOperations.Token(separators, source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1_000_000, result.Token.Length);
        Assert.Equal("tail", result.Remainder.Text);
        Assert.True(allocated < 2L * source.Length + 65536, $"確保量: {allocated}");
    }

    [Fact]
    public async Task ErrorsAndCancellationKeepSourceEffectsSeparateFromResultPublication()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("CONST s=\"a,b\"\nTOKEN(\",\",s)"));
        Assert.Equal(2, error.Line);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("s=\"a,b\"\nPRINT TOKEN(Tick(),s)\nFUNCTION Tick()\n"
                + "PRINT 1\nRESULT=\",\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }
}
