using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class LogOptionTests
{
    [Fact]
    public async Task LeadingAndRuntimeOptionsEvaluateInOrder()
    {
        var host = new LogHost();
        await new Interpreter().RunAsync("""
            OPTION EXPLICIT, LOGPATH="a,b.log", LOGLINES=2
            PUBLIC count=3
            DIM value=4
            OPTION LOGFILE="6"
            IF TRUE THEN OPTION LOGLINES=count
            Change(value)
            PROCEDURE Change(value)
                OPTION LOGLINES=value
            FEND
            """, host);
        Assert.Equal(new ScriptLogSettings[]
        {
            new(Path: "a,b.log"), new(MaximumLines: 2), new(FileMode: 6),
            new(MaximumLines: 3), new(MaximumLines: 4)
        }, host.Settings);
    }

    [Theory]
    [InlineData("OPTION LOGLINES=-1")]
    [InlineData("OPTION LOGLINES=100001")]
    [InlineData("OPTION LOGLINES=1.5")]
    [InlineData("OPTION LOGFILE=8")]
    [InlineData("OPTION LOGFILE=-1")]
    [InlineData("OPTION LOGFILE=2.5")]
    [InlineData("OPTION LOGFILE=NULL")]
    [InlineData("OPTION LOGFILE=NOTHING")]
    [InlineData("OPTION LOGFILE=\"bad\"")]
    [InlineData("OPTION LOGFILE=\"1e+300\"")]
    [InlineData("OPTION LOGFILE=\"1.5\"")]
    [InlineData("OPTION LOGFILE=1e+300*1e+300")]
    [InlineData("OPTION LOGPATH=NULL")]
    [InlineData("OPTION LOGPATH=123")]
    [InlineData("OPTION LOGPATH=EMPTY")]
    [InlineData("OPTION LOGPATH=NOTHING")]
    [InlineData("OPTION LOGPATH=TRUE")]
    [InlineData("OPTION LOGPATH=FALSE")]
    [InlineData("OPTION LOGPATH=\"a<#CR>b\"")]
    [InlineData("OPTION LOGPATH=\"a\"+CHR(0)+\"b\"")]
    [InlineData("OPTION LOGPATH=\"a<#TAB>b\"")]
    [InlineData("OPTION LOGLINES=NOTHING")]
    [InlineData("OPTION LOGLINES=NULL")]
    [InlineData("OPTION LOGLINES=\"bad\"")]
    [InlineData("OPTION LOGLINES=\"1.5\"")]
    [InlineData("OPTION LOGLINES=1e+300*1e+300")]
    public async Task InvalidValueReportsOptionLineWithoutConfiguringHost(string source)
    {
        var host = new LogHost();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("\n" + source, host));
        Assert.Equal(2, error.Line);
        Assert.Empty(host.Settings);
    }

    [Fact]
    public async Task FileModeFailureStopsLaterExpressionsButKeepsEarlierSettings()
    {
        var host = new LogHost();
        await new Interpreter().RunAsync("""
            TRY
                OPTION LOGFILE=2, LOGFILE=8, LOGPATH=1/0
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            """, host);
        Assert.Equal([new ScriptLogSettings(FileMode: 2)], host.Settings);
        Assert.Equal(["2"], host.Output);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    [InlineData("5", 5)]
    [InlineData("6", 6)]
    [InlineData("7", 7)]
    [InlineData("\"6\"", 6)]
    [InlineData("TRUE", 1)]
    [InlineData("FALSE", 0)]
    [InlineData("EMPTY", 0)]
    [InlineData("-0", 0)]
    [InlineData("\" 2 \"", 2)]
    [InlineData("\"2.0\"", 2)]
    [InlineData("\"2e0\"", 2)]
    public async Task FileModeUsesFiniteIntegralNumericConversion(
        string expression, int expected)
    {
        var host = new LogHost();
        await new Interpreter().RunAsync(
            "OPTION LOGFILE=" + expression, host);
        Assert.Equal([new ScriptLogSettings(FileMode: expected)], host.Settings);
    }

    [Fact]
    public async Task LineLimitUsesNumericConversionAtBothBoundaries()
    {
        var host = new LogHost();
        await new Interpreter().RunAsync("""
            OPTION LOGLINES=0, LOGLINES=100000
            OPTION LOGLINES="2", LOGLINES=TRUE
            OPTION LOGLINES=FALSE, LOGLINES=EMPTY
            """, host);
        Assert.Equal(new ScriptLogSettings[]
        {
            new(MaximumLines: 0), new(MaximumLines: 100000),
            new(MaximumLines: 2), new(MaximumLines: 1),
            new(MaximumLines: 0), new(MaximumLines: 0)
        }, host.Settings);
    }

    [Fact]
    public async Task HostWithoutCapabilityRejectsBeforeEvaluatingExpression()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("OPTION LOGLINES=1/0", _ => { }));
        Assert.Contains("host", error.Message);
    }


    [Fact]
    public async Task ArgumentFailureCanBeCaughtAndLaterOptionsAreNotEvaluated()
    {
        var host = new LogHost { Failure = new ArgumentException("bad path") };
        await new Interpreter().RunAsync("""
            TRY
                OPTION LOGPATH="bad", LOGLINES=1/0
            EXCEPT
                PRINT TRY_ERRLINE
            ENDTRY
            """, host);
        Assert.Equal(["2"], host.Output);
        Assert.Empty(host.Settings);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("encoder")]
    [InlineData("decoder")]
    public async Task StorageHostFailurePreservesIdentityAndBypassesScriptCatch(string kind)
    {
        Exception failure = kind switch
        {
            "encoder" => new EncoderFallbackException("encode failed"),
            "decoder" => new DecoderFallbackException("decode failed"),
            _ => new IOException("flush failed")
        };
        var host = new LogHost { Failure = failure };
        Exception? error = await Record.ExceptionAsync(() =>
            new Interpreter().RunAsync("""
                TRY
                    OPTION LOGLINES=1
                EXCEPT
                    PRINT "not reached"
                ENDTRY
                """, host));
        Assert.Same(failure, error);
        Assert.Empty(host.Output);
    }

    [Fact]
    public async Task LeadingSpecialCharacterPolicyAppliesToLogPath()
    {
        var host = new LogHost();
        await new Interpreter().RunAsync("""
            OPTION LOGPATH="<#TAB>.log", SPECIALCHAR
            """, host);
        Assert.Equal([new ScriptLogSettings(Path: "<#TAB>.log")], host.Settings);
    }

    [Fact]
    public async Task LogOptionNamesAreNotReservedVariableNames()
    {
        var host = new LogHost();
        await new Interpreter().RunAsync("""
            DIM LOGPATH=2
            DIM LOGFILE=3
            DIM LOGLINES=4
            PRINT LOGPATH+LOGFILE+LOGLINES
            """, host);
        Assert.Equal(["9"], host.Output);
        Assert.Empty(host.Settings);
    }

    [Theory]
    [InlineData("OPTION LOGPATH")]
    [InlineData("OPTION LOGFILE=")]
    [InlineData("PRINT 1\nOPTION SAMESTR")]
    [InlineData("OPTION LOGPATH=\"a\", UNKNOWN=1")]
    public void InvalidSyntaxIsRejected(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    private sealed class LogHost : IScriptLogHost
    {
        internal List<ScriptLogSettings> Settings { get; } = [];

        internal List<string> Output { get; } = [];

        internal Exception? Failure
        {
            get; init;
        }

        public ValueTask ConfigureLogAsync(ScriptLogSettings settings,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Settings.Add(settings);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.Delay(duration, token);
    }
}
