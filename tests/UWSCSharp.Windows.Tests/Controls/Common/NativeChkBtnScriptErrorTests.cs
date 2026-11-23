using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeChkBtnScriptErrorTests
{
    private sealed class Platform(string stage, CancellationTokenSource cancel)
        : ToolbarOperationTests.RecordingPlatform
    {
        internal NativeClkItemScriptErrorTests.FailedOpen Failure { get; } = new();

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) =>
            stage == "open" ? new(root, Failure) : base.CreateControlBufferOwner(root);

        public override nint Send(nint window, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags) => 0;

        public override void Activate(nint window, CancellationToken token)
        {
            if (window != 77)
            {
                if (stage == "cancel")
                {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                if (stage == "host")
                {
                    throw new ArgumentException("未分類障害");
                }

                if (stage == "prepare")
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException("準備失敗"));
                }

                return;
            }

            if (stage == "open")
            {
                Assert.Equal(0, Buffers.Address);
                Restoring?.Invoke();
                return;
            }

            base.Activate(window, token);
            if (stage == "restore")
            {
                throw WindowsOperationErrors.Record(new InvalidOperationException("復元失敗"));
            }
        }
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("prepare", true)]
    [InlineData("restore", true)]
    [InlineData("restore", false)]
    [InlineData("cancel", true)]
    [InlineData("host", true)]
    public async Task QueryReportsKnownOperationFailuresAndKeepsHostAndCancellation(string stage, bool catches)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "target");
        using var cancel = new CancellationTokenSource();
        int restores = 0;
        var platform = new Platform(stage, cancel) { Restoring = () => restores++ };
        var runner = new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform));
        string call = "PRINT CHKBTN(id, \"target\")\n";
        string source = $"id = HNDTOID({(long)fixture.Root})\n"
            + (catches ? "TRY\n" + call
                + "EXCEPT\nPRINT TRY_ERRLINE\nPRINT TRY_ERRMSG\nENDTRY\n" : call)
            + "PRINT \"after\"";
        List<string> output = [];
        if (stage == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync(source, output.Add, cancel.Token));
            Assert.Empty(output);
        }
        else if (stage == "host")
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                runner.RunAsync(source, output.Add, default));
            Assert.Empty(output);
        }
        else if (!catches)
        {
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
                runner.RunAsync(source, output.Add, default));
            Assert.Contains("FucCLKITEM:復元失敗", failure.Message);
            Assert.Empty(output);
        }
        else
        {
            await runner.RunAsync(source, output.Add, default);
            Assert.Equal("3", output[0]);
            Assert.Contains("FucCLKITEM:", output[1]);
            Assert.Equal("after", output[2]);
            Assert.Equal(3, output.Count);
        }

        Assert.Equal(1, restores);
        Assert.Equal(stage == "open" ? 1 : 0, platform.Failure.Opens);
        if (stage != "open")
        {
            Assert.True(platform.Buffers.Released);
            Assert.True(platform.Buffers.Closed);
        }

        Assert.Null(fixture.CallbackFailure);
    }
}
