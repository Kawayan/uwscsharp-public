using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeClkItemScriptErrorTests
{
    internal sealed class FailedOpen : INativeControlBufferPlatform
    {
        internal int Opens;

        public nint Open(nint root)
        {
            Opens++;
            Marshal.SetLastPInvokeError(5);
            return 0;
        }

        public nint Allocate(nint process) => throw new Xunit.Sdk.XunitException("確保不可");

        public void Free(nint process, nint address) => throw new Xunit.Sdk.XunitException("解放不可");

        public void Close(nint process) => throw new Xunit.Sdk.XunitException("close不可");
    }

    private sealed class Platform(bool failOpen) : ToolbarOperationTests.RecordingPlatform
    {
        internal FailedOpen Failure { get; } = new();

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) =>
            failOpen ? new(root, Failure) : base.CreateControlBufferOwner(root);
    }

    [Fact]
    public async Task WorkerCapacityTimeoutReachesScriptExceptAndSlotsRecover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { });
        using var entered = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        List<Task<int>> pending = [];
        try
        {
            for (int index = 0; index < 4; index++)
            {
                pending.Add(NativeControlWorker.RunAsync(_ =>
                {
                    entered.Signal();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    return 1;
                }, default, TimeSpan.FromSeconds(10)));
            }

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var platform = new ToolbarOperationTests.RecordingPlatform();
            var automation = new WindowsAutomation(_ => throw new InvalidOperationException(),
                listSelectionPlatform: platform);
            string source = $"id = HNDTOID({(long)fixture.Root})\nTRY\n"
                + "CLKITEM(id, \"target\", 64)\nEXCEPT\nPRINT TRY_ERRMSG\nENDTRY\n"
                + "PRINT \"after\"";
            List<string> output = [];
            await new Interpreter(windows: automation).RunAsync(source, output.Add, default);
            Assert.Contains("FucCLKITEM:ネイティブcontrolの待機枠が満杯です。", output[0]);
            Assert.Equal("after", output[1]);
            Assert.Equal(2, output.Count);
            Assert.Equal(4, NativeControlWorker.Active);
            Assert.Equal(0, platform.Buffers.Address);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(pending);
        }

        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("capacity")]
    [InlineData("overflow")]
    public async Task NativeFailureReachesExceptWithSourceLine(string failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 701);
        int getters = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message != 0x44b)
            {
                return null;
            }

            getters += pointer != 0 ? 1 : 0;
            return 65535;
        });
        bool failOpen = failure == "open";
        var platform = new Platform(failOpen);
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException(),
            listSelectionPlatform: platform);
        List<string> output = [];
        string source = $"id = HNDTOID({(long)fixture.Root})\nTRY\n"
            + $"PRINT CLKITEM(id, \"target\", 64, TRUE, {(failure == "overflow" ? int.MinValue : 1)})\nEXCEPT\n"
            + "PRINT TRY_ERRLINE\nPRINT TRY_ERRMSG\nENDTRY\nPRINT \"after\"";
        await new Interpreter(windows: automation).RunAsync(source, output.Add, default);
        Assert.Equal("3", output[0]);
        Assert.Contains("FucCLKITEM:", output[1]);
        string expected = failure switch
        {
            "open" => new Win32Exception(5).Message,
            "overflow" => new OverflowException().Message,
            _ => "TOOLBAR文字列の取得容量を超えています。",
        };
        Assert.Contains(expected, output[1]);
        Assert.Equal("after", output[2]);
        Assert.Equal(3, output.Count);
        Assert.Equal(0, getters);
        Assert.Empty(platform.Posts);
        Assert.Equal(failOpen ? 1 : 0, platform.Failure.Opens);
        if (failure == "capacity")
        {
            Assert.True(platform.Buffers.Released);
            Assert.True(platform.Buffers.Closed);
        }

        Assert.Null(fixture.CallbackFailure);
    }
}
