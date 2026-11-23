using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTreeSelectionTests
{
    private sealed class Sequence(CancellationTokenSource cancel, string? stop = null)
        : OwnedListSelectionPlatform
    {
        internal List<string> Events { get; } = [];
        internal int Probes;
        internal bool Selected;
        private int tens;
        private int pumps;

        public override bool IsWindow(nint target) => true;

        public override nint Send(nint target, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            Assert.Equal(11, target);
            if (timed)
            {
                Assert.Equal(0u, message);
                Assert.Equal(8000u, timeout);
                Assert.Equal(0u, flags);
                Events.Add("probe");
                Probes++;
                if (stop == "pre_probe" || (stop == "post_probe" && Probes == 2))
                {
                    cancel.Cancel();
                }
            }
            else
            {
                Assert.Equal(0x110bu, message);
                Assert.Equal(9, parameter);
                Assert.Equal(22, data);
                Events.Add("select");
                Selected = true;
                if (stop == "select")
                {
                    cancel.Cancel();
                }
            }

            return stop == "reject" ? 0 : 1;
        }

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Events.Add($"sleep:{milliseconds}");
            tens += milliseconds == 10 ? 1 : 0;
            if (Selected && (stop == "delay" || (stop == "yield" && milliseconds == 0)))
            {
                cancel.Cancel();
            }

            if (Selected && stop == "throw_delay")
            {
                throw new InvalidOperationException("delay callback");
            }

            token.ThrowIfCancellationRequested();
        }

        public override void Pump(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Events.Add("pump");
            pumps++;
            if (stop == "process" && tens == 10 && pumps == 3)
            {
                cancel.Cancel();
            }

            token.ThrowIfCancellationRequested();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionIsBetweenTargetPreparationAndTenWaitsThenPumping(bool rejected)
    {
        using var cancel = new CancellationTokenSource();
        var platform = new Sequence(cancel, rejected ? "reject" : null);
        Assert.Equal(rejected ? 0 : 1, NativeTreeSelection.Select(11, 22, platform, default));
        Assert.Equal(["sleep:0", "pump", "probe", "sleep:0", "pump", "select"],
            platform.Events.Take(6));
        Assert.Equal(Enumerable.Repeat("sleep:10", 10), platform.Events.Skip(6).Take(10));
        Assert.Equal("pump", platform.Events[16]);
        Assert.Equal(Enumerable.Range(0, 44).Select(index => index % 2 == 0 ? "sleep:0" : "pump"),
            platform.Events.Skip(17).Take(44));
        Assert.Equal(["sleep:0", "pump", "probe", "sleep:0", "pump"], platform.Events.TakeLast(5));
        Assert.Equal(66, platform.Events.Count);
    }

    [Theory]
    [InlineData("pre_probe", false)]
    [InlineData("select", true)]
    [InlineData("delay", true)]
    [InlineData("process", true)]
    [InlineData("yield", true)]
    [InlineData("post_probe", true)]
    public void CancellationStopsRemainingPhases(string stage, bool selected)
    {
        using var cancel = new CancellationTokenSource();
        var platform = new Sequence(cancel, stage);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            NativeTreeSelection.Select(11, 22, platform, cancel.Token));
        Assert.Equal(selected, platform.Selected);
        Assert.Equal(stage == "post_probe" ? 2 : 1, platform.Probes);
    }

    [Fact]
    public void DelayExceptionLeavesCompletedSelection()
    {
        using var cancel = new CancellationTokenSource();
        var platform = new Sequence(cancel, "throw_delay");
        Assert.Throws<InvalidOperationException>(() =>
            NativeTreeSelection.Select(11, 22, platform, default));
        Assert.True(platform.Selected);
        Assert.Equal(1, platform.Probes);
    }

    private sealed class Lifetime(string? failure, CancellationTokenSource cancel)
        : OwnedListSelectionPlatform
    {
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Restored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) => new(root, Buffers);

        public override bool IsVisible(nint target) => false;

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint root, CancellationToken token)
        {
            Assert.Equal(77, root);
            Assert.True(Buffers.Released);
            Assert.True(Buffers.Closed);
            Restored.TrySetResult();
        }

        public override nint Send(nint target, uint message, nint parameter,
            nint data, bool timed, uint timeout, uint flags)
        {
            if (timed)
            {
                if (failure == "blocked_probe")
                {
                    Entered.TrySetResult();
                    Assert.True(Release.Wait(TimeSpan.FromSeconds(5)));
                }

                return 1;
            }

            return base.Send(target, message, parameter, data, timed, timeout, flags);
        }

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (milliseconds == 10)
            {
                if (failure == "cancel")
                {
                    cancel.Cancel();
                }

                if (failure == "throw")
                {
                    throw new InvalidOperationException("delay callback");
                }
            }

            token.ThrowIfCancellationRequested();
        }

        public override void Pump(CancellationToken token) => token.ThrowIfCancellationRequested();
    }

    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    public async Task DelayFailureKeepsNativeSelectionAndReleasesBeforeRestore(bool query, string failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint item = TreeControlTests.Add(tree, 0, "item");
        using var cancel = new CancellationTokenSource();
        var platform = new Lifetime(failure, cancel);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = query ? [id, Value.String("item")] : [id, Value.String("item"), Value.Numeric(16)];
        if (failure == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync(query ? "CHKBTN" : "CLKITEM", args, cancel.Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync(query ? "CHKBTN" : "CLKITEM", args, cancel.Token));
        }

        Assert.Equal(item, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.True(platform.Restored.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TimedOutProbeKeepsRootBufferUntilSynchronousReturn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        var platform = new Lifetime("blocked_probe", cancel);
        using (platform.Release)
        {
            using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
            {
                SliderControlTests.InitializeControls(2);
                nint tree = host.Child("SysTreeView32", "", 0, 10);
                TreeControlTests.Add(tree, 0, "item");
            });
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Task<Value> pending = session.InvokeAsync("CHKBTN", [id, Value.String("item")], default).AsTask();
            try
            {
                await platform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Assert.ThrowsAsync<TimeoutException>(async () => await pending);
                Assert.NotEqual(0, platform.Buffers.Address);
                Assert.False(platform.Buffers.Released);
                Assert.False(platform.Restored.Task.IsCompleted);
            }
            finally
            {
                platform.Release.Set();
                await platform.Restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }
}
