using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ControlErrorClassificationTests
{
    public static TheoryData<string, int, bool> Cases
    {
        get
        {
            TheoryData<string, int, bool> result = [];
            foreach (string function in new[] { "CLKITEM", "CHKBTN" })
            {
                for (int kind = 0; kind < 12; kind++)
                {
                    result.Add(function, kind, false);
                    if (kind < 5)
                    {
                        result.Add(function, kind, true);
                    }
                }
            }

            return result;
        }
    }

    private static Exception Failure(int kind, CancellationToken token) => kind switch
    {
        0 => new InvalidOperationException("同じ本文"),
        1 => new Win32Exception(5, "同じ本文"),
        2 => new OverflowException("同じ本文"),
        3 => new NotSupportedException("同じ本文"),
        4 => new TimeoutException("同じ本文"),
        5 => new Win32Exception(8),
        6 => new Win32Exception(14),
        7 => new Win32Exception(1450),
        8 => new Win32Exception(1455),
        9 => new OutOfMemoryException(),
        10 => new OperationCanceledException(token),
        _ => new COMException("メモリ不足", unchecked((int)0x8007000e))
    };

    private sealed class Platform(Exception failure)
        : ToolbarOperationTests.RecordingPlatform
    {
        public override void Activate(nint window, CancellationToken token)
        {
            if (window != 77)
            {
                throw failure;
            }

            base.Activate(window, token);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnlyFailuresIdentifiedAtTheirOriginReachExcept(
        string function, int kind, bool identified)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "target");
        using var cancel = new CancellationTokenSource();
        Exception primary = Failure(kind, cancel.Token);
        if (identified)
        {
            WindowsOperationErrors.Record(primary);
        }

        int restored = 0;
        var platform = new Platform(primary) { Restoring = () => restored++ };
        var runner = new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            listSelectionPlatform: platform));
        string call = function == "CLKITEM"
            ? "CLKITEM(id,\"target\",32)" : "CHKBTN(id,\"target\")";
        string source = $"id=HNDTOID({(long)fixture.Root})\nTRY\nTRY\n{call}\n"
            + "EXCEPT\nPRINT TRY_ERRLINE\nPRINT TRY_ERRMSG\nENDTRY\n"
            + "FINALLY\nPRINT \"cleanup\"\nENDTRY\nPRINT \"after\"";
        List<string> output = [];
        if (identified)
        {
            await runner.RunAsync(source, output.Add);
            Assert.Equal(["4", "4行: FucCLKITEM:" + primary.Message,
                "cleanup", "after"], output);
        }
        else
        {
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
                runner.RunAsync(source, output.Add));
            Assert.Same(primary, actual);
            Assert.Equal(["cleanup"], output);
            if (actual is OperationCanceledException cancelled)
            {
                Assert.Equal(cancel.Token, cancelled.CancellationToken);
            }
        }

        Assert.Equal(1, restored);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("CLKITEM")]
    [InlineData("CHKBTN")]
    public async Task MapperDoesNotTrustTypesOrGlobalMarkersWithoutSessionInvocation(
        string function)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        for (int kind = 0; kind < 12; kind++)
        {
            Exception failure = Failure(kind, default);
            Assert.Null(session.GetScriptErrorMessage(function, failure));
            WindowsOperationErrors.Record(failure);
            Assert.Null(session.GetScriptErrorMessage(function, failure));
        }
    }

    [Theory]
    [InlineData("CLKITEM", "CHKBTN")]
    [InlineData("CHKBTN", "CLKITEM")]
    public async Task IdentifiedFailureBelongsToInvokingSessionAndFunction(
        string function, string otherFunction)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "target");
        InvalidOperationException primary = WindowsOperationErrors.Record(
            new InvalidOperationException("同じ本文"));
        var platform = new Platform(primary) { Restoring = () => { } };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            listSelectionPlatform: platform).CreateSession();
        await using IWindowsAutomationSession other = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Value[] arguments = function == "CLKITEM"
            ? [id, Value.String("target"), Value.Numeric(32)]
            : [id, Value.String("target")];
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync(function, arguments, default).AsTask());
        Assert.Same(primary, actual);
        Assert.Equal("FucCLKITEM:同じ本文",
            session.GetScriptErrorMessage(function.ToLowerInvariant(), actual));
        Assert.Null(session.GetScriptErrorMessage(otherFunction, actual));
        Assert.Null(other.GetScriptErrorMessage(function, actual));
        Assert.Null(session.GetScriptErrorMessage(function,
            new InvalidOperationException("同じ本文")));
    }
}
