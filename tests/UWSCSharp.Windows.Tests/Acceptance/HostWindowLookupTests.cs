using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class HostWindowLookupTests
{
    [Fact]
    public async Task CapturedScheduleWindowChecksNativeOwnerAndSurvivesFork()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        uint thread = Native.GetWindowThreadProcessId(fixture.Root, out uint process);
        var capture = new WindowsCapturedWindow(fixture.Root, process, thread);
        WindowsAutomation automation = CapturedAutomation(capture);
        await using IWindowsAutomationSession root = automation.CreateSession();
        await using IWindowsAutomationSession fork = root.Fork();
        Value[] args = [Value.String(BuiltinWindowSelectors.GetScheduleWin)];
        Value first = await root.InvokeAsync(BuiltinFunctionNames.GetId, args, default);
        Value second = await fork.InvokeAsync(BuiltinFunctionNames.GetId, args, default);
        Assert.True(first.AsNumber() > 0);
        Assert.Equal(first.AsNumber(), second.AsNumber());

        var mismatched = new WindowsCapturedWindow(fixture.Root, checked(process + 1), thread);
        await using IWindowsAutomationSession other = CapturedAutomation(mismatched).CreateSession();
        Assert.Equal(-1, (await other.InvokeAsync(BuiltinFunctionNames.GetId, args, default)).AsNumber());
    }

    [Fact]
    public async Task DestroyedCapturedScheduleWindowDoesNotUseAnotherHostWindow()
    {
        PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        uint thread = Native.GetWindowThreadProcessId(fixture.Root, out uint process);
        var capture = new WindowsCapturedWindow(fixture.Root, process, thread);
        await fixture.DisposeAsync();
        var output = new List<string>();
        await new Interpreter(windows: CapturedAutomation(capture)).RunAsync(
            "PRINT GETID(GET_SCHEDULE_WIN)", output.Add);
        Assert.Equal(new[] { "-1" }, output);
    }

    private static WindowsAutomation CapturedAutomation(WindowsCapturedWindow capture) =>
        WindowsAutomation.WithAsyncHostWindows(
            (_, _) => throw new InvalidOperationException("他の窓へ置き換えません。"),
            (_, send) => send(), new WindowsCommandOptions(),
            new WindowsSelectiveInputCleanup(), capture);

    [Theory]
    [InlineData("GET_THISUWSC_WIN", WindowsHostWindow.Main)]
    [InlineData("GET_LOGPRINT_WIN", WindowsHostWindow.Log)]
    [InlineData("GET_FORM_WIN", WindowsHostWindow.Form)]
    [InlineData("GET_FORM_WIN2", WindowsHostWindow.Form2)]
    [InlineData("GET_SCHEDULE_WIN", WindowsHostWindow.Schedule)]
    [InlineData("GET_STOPFORM_WIN", WindowsHostWindow.Stop)]
    public async Task QueryRoutesToHostAndKeepsIdStable(string selector, WindowsHostWindow expected)
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        List<WindowsHostWindow> requests = [];
        var automation = WindowsAutomation.WithHostWindows((kind, token) =>
        {
            token.ThrowIfCancellationRequested();
            requests.Add(kind);
            return ValueTask.FromResult(fixture.Root);
        });
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            $"id=GETID({selector},\"absent-class\",-1,\"absent-child\")\n"
            + $"PRINT IDTOHND(id)\nPRINT GETID({selector})=id", output.Add);
        Assert.Equal([((long)fixture.Root).ToString(), "1"], output);
        Assert.Equal([expected, expected], requests);
    }

    [Theory]
    [InlineData("GET_THISUWSC_WIN")]
    [InlineData("GET_LOGPRINT_WIN")]
    [InlineData("GET_BALLOON_WIN")]
    [InlineData("GET_FUKIDASI_WIN")]
    [InlineData("GET_FORM_WIN")]
    [InlineData("GET_FORM_WIN2")]
    [InlineData("GET_SCHEDULE_WIN")]
    [InlineData("GET_STOPFORM_WIN")]
    public async Task MissingHostReturnsNotFoundWithoutIndefiniteWait(string selector)
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            $"PRINT GETID({selector},,-1)", output.Add).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["-1"], output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidHostHandleIsNotRegistered(long handle)
    {
        var automation = WindowsAutomation.WithHostWindows((_, _) =>
            ValueTask.FromResult((nint)handle));
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PRINT GETID(GET_THISUWSC_WIN)", output.Add);
        Assert.Equal(["-1"], output);
    }

    [Fact]
    public async Task DestroyedHostHandleIsNotRegistered()
    {
        PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        nint handle = fixture.Root;
        await fixture.DisposeAsync();
        var automation = WindowsAutomation.WithHostWindows((_, _) =>
            ValueTask.FromResult(handle));
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PRINT GETID(GET_THISUWSC_WIN)", output.Add);
        Assert.Equal(["-1"], output);
    }

    [Fact]
    public async Task PlainConstantNameIsAnOrdinaryTitle()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, title: "GET_THISUWSC_WIN");
        bool requested = false;
        var automation = WindowsAutomation.WithHostWindows((_, _) =>
        {
            requested = true;
            return ValueTask.FromResult((nint)0);
        });
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PRINT IDTOHND(GETID(\"GET_THISUWSC_WIN\",\"STATIC\",0.01))", output.Add);
        Assert.Equal([((long)fixture.Root).ToString()], output);
        Assert.False(requested);
    }

    [Fact]
    public async Task LowercaseSelectorLiteralQueriesHost()
    {
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var automation = WindowsAutomation.WithHostWindows((_, _) => ValueTask.FromResult(fixture.Root));
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync(
            "PRINT IDTOHND(GETID(\"__get_thisuwsc_win__\"))", output.Add);
        Assert.Equal([((long)fixture.Root).ToString()], output);
    }

    [Fact]
    public async Task CancellationAndDisposalAfterHostReplyAreObserved()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelled = WindowsAutomation.WithHostWindows((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult((nint)0);
        });
        await using IWindowsAutomationSession first = cancelled.CreateSession();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await first.InvokeAsync(BuiltinFunctionNames.GetId,
                [Value.String(BuiltinWindowSelectors.GetThisUwscWin)], cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);

        var reply = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = WindowsAutomation.WithHostWindows((_, _) => new ValueTask<nint>(reply.Task));
        IWindowsAutomationSession second = delayed.CreateSession();
        Task<Value> lookup = second.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(BuiltinWindowSelectors.GetThisUwscWin)], default).AsTask();
        await second.DisposeAsync();
        reply.SetResult(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lookup);
    }

    [Fact]
    public async Task InvalidWaitDoesNotQueryHostAndUnexpectedHostFailureBypassesCatch()
    {
        bool requested = false;
        var failure = new IOException("実行元の取得失敗");
        var automation = WindowsAutomation.WithHostWindows((_, _) =>
        {
            requested = true;
            return ValueTask.FromException<nint>(failure);
        });
        await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: automation).RunAsync("GETID(GET_THISUWSC_WIN,,-2)", _ => { }));
        Assert.False(requested);
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            new Interpreter(windows: automation).RunAsync(
                "TRY\nGETID(GET_THISUWSC_WIN)\nEXCEPT\nPRINT \"caught\"\nENDTRY", _ => { }));
        Assert.Same(failure, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedSpecialLookupWaitsForResumeOrCancellation(bool cancel)
    {
        using var pause = new ScriptPauseControl();
        using var cancellation = new CancellationTokenSource();
        bool requested = false;
        var automation = WindowsAutomation.WithHostWindows((_, _) =>
        {
            requested = true;
            return ValueTask.FromResult((nint)0);
        });
        await using IWindowsAutomationSession session = automation.CreateSession();
        session.ConfigurePause(pause, cancellation.Token);
        pause.Pause();
        Task<Value> lookup = session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(BuiltinWindowSelectors.GetThisUwscWin)], cancellation.Token).AsTask();
        Assert.True(pause.IsPaused);
        Assert.False(requested);
        Assert.False(lookup.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
            Assert.False(requested);
        }
        else
        {
            pause.Resume();
            Assert.Equal(-1, (await lookup.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber());
            Assert.True(requested);
        }
    }
}
