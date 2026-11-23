using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ControlRestoreScriptTests
{
    public static TheoryData<string, string, bool> Cases
    {
        get
        {
            TheoryData<string, string, bool> result = [];
            foreach (string function in new[] { "CLKITEM", "CHKBTN" })
            {
                foreach (string kind in new[] { "operation", "host", "cancel" })
                {
                    result.Add(function, kind, false);
                    result.Add(function, kind, true);
                }
            }

            return result;
        }
    }

    private sealed class Platform(Exception primary, Exception restore,
        bool worker) : ToolbarOperationTests.RecordingPlatform
    {
        public override uint WindowThread(nint window) => worker ? 2u : 1u;

        public override uint CurrentThread() => 1;

        public override void Activate(nint window, CancellationToken token)
        {
            if (window == 77)
            {
                Assert.False(token.CanBeCanceled);
                base.Activate(window, token);
                throw restore;
            }

            throw primary;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ScriptClassificationUsesOperationFailureDespiteRestoreFailure(
        string function, string kind, bool worker)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint view = ListViewControlTests.Create(host, 10);
            ListViewControlTests.SetText(view, 0, "target");
        });
        using var cancel = new CancellationTokenSource();
        Exception primary = kind == "cancel"
            ? new OperationCanceledException(cancel.Token)
            : new InvalidOperationException("操作失敗");
        if (kind == "operation")
        {
            WindowsOperationErrors.Record(primary);
        }

        InvalidOperationException restore = WindowsOperationErrors.Record(
            new InvalidOperationException("復元失敗"));
        int restored = 0;
        var platform = new Platform(primary, restore, worker)
        {
            Restoring = () => restored++
        };
        var runner = new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            listSelectionPlatform: platform));
        string call = function == "CLKITEM"
            ? "CLKITEM(id,\"target\",32)" : "CHKBTN(id,\"target\")";
        string script = $"id=HNDTOID({(long)fixture.Root})\nTRY\nTRY\n{call}\n"
            + "EXCEPT\nPRINT TRY_ERRLINE\nPRINT TRY_ERRMSG\nENDTRY\n"
            + "FINALLY\nPRINT \"cleanup\"\nENDTRY\nPRINT \"after\"";
        List<string> output = [];
        if (kind == "operation")
        {
            await runner.RunAsync(script, output.Add);
            Assert.Equal(["4", "4行: FucCLKITEM:操作失敗", "cleanup", "after"], output);
        }
        else
        {
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
                runner.RunAsync(script, output.Add));
            Assert.Same(primary, actual);
            Assert.Equal(["cleanup"], output);
            if (actual is OperationCanceledException cancelled)
            {
                Assert.Equal(cancel.Token, cancelled.CancellationToken);
            }
        }

        AggregateException errors = Assert.IsType<AggregateException>(
            primary.Data[NativeControlOperation.RestoreErrorsKey]);
        Assert.Same(restore, Assert.Single(errors.InnerExceptions));
        Assert.Equal(1, restored);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
    }
}
