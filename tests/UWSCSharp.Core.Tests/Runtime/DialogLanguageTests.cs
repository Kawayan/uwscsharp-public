using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DialogLanguageTests
{
    [Fact]
    public async Task StartupFontPrecedesPublicInitializersAndCanBeOverridden()
    {
        var adapter = new Adapter();
        await new Interpreter(windows: adapter).RunAsync("""
            PUBLIC answer=MSGBOX("initializer")
            OPTION DEFAULTFONT="Consolas,11"
            MSGBOX("after")
            """, _ => { }, default,
            new ScriptRunOptions([]) { DefaultFont = "Arial,18" });
        Assert.Equal(["Arial,18", "Consolas,11"], adapter.FontsAtMessage);
        adapter.Options.Clear();
        adapter.FontsAtMessage.Clear();
        await new Interpreter(windows: adapter).RunAsync("MSGBOX(\"next\")", _ => { });
        Assert.Equal(["default"], adapter.FontsAtMessage);
    }

    [Theory]
    [InlineData("OPTION FIXBALLOON")]
    [InlineData("OPTION POSITION=10,20,FIXBALLOON")]
    [InlineData("OPTION fixballoon,DEFAULTFONT=\"Arial,18\"")]
    public async Task FixedBalloonIsPassedAsAnArgumentlessDialogOption(string option)
    {
        var adapter = new Adapter();
        await new Interpreter(windows: adapter).RunAsync(
            option + "\nMSGBOX(\"probe\")", new PositionHost(_ => { }));
        (string Name, Value[] Values) setting = Assert.Single(
            adapter.Options, item => item.Name == Keywords.FixBalloon);
        Assert.Empty(setting.Values);
        Assert.NotNull(adapter.Request);
    }

    [Theory]
    [InlineData("OPTION FIXBALLOON=TRUE")]
    [InlineData("OPTION FIXBALLOON=0")]
    [InlineData("OPTION POSITION=10,FIXBALLOON")]
    public void FixedBalloonRejectsAnAssignmentAndDoesNotConsumeAMissingCoordinate(string source)
        => Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));

    [Fact]
    public void FixedBalloonKeepsThePreviousAppearanceImmutable()
    {
        var first = new DialogAppearance("default");
        DialogAppearance next = first.Apply(Keywords.FixBalloon, []);
        Assert.False(first.FixedBalloon);
        Assert.True(next.FixedBalloon);
        Assert.True(next.Apply(Keywords.DlgTitle, [Value.String("changed")]).FixedBalloon);
        Assert.Throws<InvalidOperationException>(() => first.Apply(
            Keywords.FixBalloon, [Value.Numeric(1)]));
    }

    [Fact]
    public void FixedBalloonExampleHasNoStaticCallDiagnostics()
    {
        const string source = """
            OPTION FIXBALLOON
            BALLOON("desktop probe",80,90,0,16,"Arial",0,$FFFFFF,0)
            SLEEP(0.1)
            BALLOON()
            """;
        _ = ScriptCompiler.CompileWithoutExternalFiles(source);
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze(source);
        Assert.Empty(analysis.Diagnostics);
        Assert.False(analysis.Truncated);
        Assert.Equal(0, analysis.UncheckedSyntaxNodes);
        Assert.Equal(0, analysis.UncheckedDynamicCalls);
        Assert.Equal(0, analysis.UncheckedMemberAccesses);
    }

    [Fact]
    public async Task EvaluatesOptionsInOrderAndPassesOmittedCoordinates()
    {
        var adapter = new Adapter();
        List<string> output = [];
        var host = new PositionHost(output.Add);
        await new Interpreter(windows: adapter).RunAsync("""
            OPTION DLGTITLE="確認", DEFAULTFONT="Arial,18", POSITION=-100,120
            PRINT MSGBOX("message", BTN_YES+BTN_NO, , ,BTN_NO,TRUE)
            """, host);
        Assert.Equal(["8"], output);
        Assert.Equal([Keywords.DlgTitle, Keywords.DefaultFont],
            adapter.Options.Select(option => option.Name));
        Assert.Equal(new ScriptWindowPosition(-100, 120), Assert.Single(host.Positions));
        Assert.Equal("message", adapter.Request!.Message);
        Assert.Null(adapter.Request.X);
        Assert.Null(adapter.Request.Y);
        Assert.True(adapter.Request.Links);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public async Task RuntimeOptionAndReuseDoNotLeakStateAcrossRuns()
    {
        var adapter = new Adapter();
        var interpreter = new Interpreter(windows: adapter);
        ScriptProgram program = ScriptCompiler.Compile("""
            OPTION DLGTITLE="first"
            OPTION DLGTITLE="second"
            PRINT MSGBOX("確認")
            """);
        await interpreter.RunAsync(program, new CallbackScriptHost(_ => { }));
        Assert.Equal(["first", "second"], adapter.Options.Select(option => option.Values[0].Text));
        adapter.Options.Clear();
        await interpreter.RunAsync("PRINT MSGBOX(123)", _ => { });
        Assert.Empty(adapter.Options);
        Assert.Equal("123", adapter.Request!.Message);
    }

    [Theory]
    [InlineData("MSGBOX()")]
    [InlineData("MSGBOX(,)")]
    [InlineData("MSGBOX(Probe(),1,2,3,4,5,Probe())")]
    public async Task ArityIsCheckedBeforeAnyArgumentEffects(string call)
    {
        var adapter = new Adapter();
        List<string> output = [];
        await new Interpreter(windows: adapter).RunAsync($$"""
            TRY
                {{call}}
            EXCEPT
                PRINT "caught"
            ENDTRY
            FUNCTION Probe()
                PRINT "side"
                RESULT="text"
            FEND
            """, output.Add);
        Assert.Equal(["caught"], output);
        Assert.Null(adapter.Request);
    }

    [Theory]
    [InlineData("OPTION POSITION=100")]
    [InlineData("OPTION POSITION=100,")]
    [InlineData("OPTION DLGTITLE=")]
    public void RejectsIncompleteOptionAssignments(string source)
        => Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));

    [Fact]
    public void AppearanceCopiesSettingsWithoutChangingItsPreviousSnapshot()
    {
        var first = new DialogAppearance("default");
        DialogAppearance next = first.Apply(Keywords.DefaultFont, [Value.String("Arial,18")]);
        Assert.Equal("Tahoma", first.FontFamily);
        Assert.Equal(9, first.FontSize);
        Assert.Equal("Arial", next.FontFamily);
        Assert.Equal(18, next.FontSize);
        Assert.Throws<InvalidOperationException>(() => first.Apply(
            Keywords.DefaultFont, [Value.String("Arial,NaN")]));
        Assert.Throws<InvalidOperationException>(() => first.Apply(
            Keywords.DefaultFont, [Value.String(",18")]));
    }

    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal List<(string Name, Value[] Values)> Options = [];
        internal MessageBoxRequest? Request;
        internal bool Disposed;
        internal List<string> FontsAtMessage = [];

        public IWindowsAutomationSession CreateSession()
        {
            Disposed = false;
            Request = null;
            return this;
        }

        public void ConfigureDialogOption(string name, IReadOnlyList<Value> values,
            CancellationToken cancellationToken) => Options.Add((name, values.ToArray()));

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> values,
            CancellationToken token)
        {
            Assert.Equal(BuiltinFunctionNames.MsgBox, name);
            Request = MessageBoxRequest.Read(values);
            FontsAtMessage.Add(Options.LastOrDefault(option => option.Name == Keywords.DefaultFont)
                .Values?.FirstOrDefault().Text ?? "default");
            return ValueTask.FromResult(Value.Numeric(Request.DefaultButton));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PositionHost(Action<string> output) : IScriptWindowPositionHost
    {
        internal List<ScriptWindowPosition> Positions = [];

        public ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
            CancellationToken token)
        {
            Positions.Add(request);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            output(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            Task.Delay(duration, token);
    }
}
