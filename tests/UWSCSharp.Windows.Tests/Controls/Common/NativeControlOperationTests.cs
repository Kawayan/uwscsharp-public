using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeControlOperationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreFailurePropagatesAndWorkerRemainsReusable(bool bodyFails)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var restoreFailure = new InvalidOperationException("復元失敗");
        var bodyFailure = new ArgumentException("処理失敗");
        var platform = new Platform { RestoreFailure = restoreFailure };
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeControlOperation.RunAsync(10, platform, _ =>
            {
                if (bodyFails)
                {
                    throw bodyFailure;
                }

                return true;
            }, default));
        Assert.Same(bodyFails ? bodyFailure : restoreFailure, actual);
        if (bodyFails)
        {
            AggregateException errors = Assert.IsType<AggregateException>(
                actual.Data[NativeControlOperation.RestoreErrorsKey]);
            Assert.Same(restoreFailure, Assert.Single(errors.InnerExceptions));
        }
        Assert.Equal(["restore:77"], platform.Events);
        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ButtonAndListShareOneSnapshotForSuccessFailureAndCancellation(int mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", mode == 1 ? "other" : "target", mode == 2 ? 2 : 3, 10);
        nint list = fixture.Child("LISTBOX", "", 1, 60);
        Native.SendText(list, 0x180, 0, "target", 2, 1000, out _);
        using var cancellation = new CancellationTokenSource();
        fixture.Clicked = (_, _) =>
        {
            if (mode == 3)
            {
                cancellation.Cancel();
            }
        };
        Native.ShowWindow(fixture.Root, 0);
        var platform = new SessionPlatform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] arguments = [id, Value.String("target"), Value.Numeric(1 | 2 | 512)];
        if (mode == 3)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", arguments, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(1),
                await session.InvokeAsync("CLKITEM", arguments, cancellation.Token));
        }

        Assert.Equal(1, platform.Captures);
        Assert.Equal([(nint)77], platform.Restores);
        Assert.Equal((nint)(mode == 1 ? 0 : -1), Native.SendMessageW(list, 0x188, 0, 0));
        Assert.Equal((nint)(mode is 0 or 3 ? 1 : 0), Native.SendMessageW(button, 0xf0, 0, 0));
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(4, false, false)]
    [InlineData(4, true, false)]
    [InlineData(8, false, false)]
    [InlineData(8, true, false)]
    [InlineData(16, false, false)]
    [InlineData(16, true, false)]
    [InlineData(32, false, false)]
    [InlineData(32, true, false)]
    [InlineData(64, false, false)]
    [InlineData(64, true, false)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(8, true, true)]
    [InlineData(16, true, true)]
    [InlineData(32, true, true)]
    [InlineData(64, true, true)]
    public async Task HiddenRootNativeKindsRestoreAfterSuccessOrMissing(int kind, bool found, bool hideChild)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint menu = 0;
        nint child = 0;
        string name = "target";
        switch (kind)
        {
            case 1:
                child = fixture.Child("BUTTON", name, 0, 10);
                break;
            case 2:
                nint list = child = fixture.Child("LISTBOX", "", 1, 10);
                Native.SendText(list, 0x180, 0, name, 2, 1000, out _);
                break;
            case 4:
                menu = MenuControlTests.CreateMenu();
                Assert.True(MenuControlTests.AppendMenuW(menu, 0, 701, name));
                Assert.True(MenuControlTests.SetMenu(fixture.Root, menu));
                break;
            case 8:
                child = TabControlTests.Create(fixture, 10, 0, "other", name);
                break;
            case 16:
                SliderControlTests.InitializeControls(2);
                nint tree = child = fixture.Child("SysTreeView32", "", 0, 10);
                TreeControlTests.Add(tree, 0, name);
                break;
            case 32:
                child = ListViewControlTests.Create(fixture, 10);
                name = "&first...";
                break;
            case 64:
                nint toolbar = child = ToolbarControlTests.Create(fixture, 10);
                ToolbarControlTests.Add(toolbar, name, 701);
                break;
        }

        if (hideChild)
        {
            Native.ShowWindow(child, 0);
        }

        Native.ShowWindow(fixture.Root, 0);
        var platform = new SessionPlatform();
        try
        {
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.Numeric(found && !hideChild ? 1 : 0), await session.InvokeAsync("CLKITEM",
                [id, Value.String(found ? name : "missing"), Value.Numeric(kind | 512)], default));
            Assert.Equal([(nint)77], platform.Restores);
        }
        finally
        {
            if (menu != 0)
            {
                MenuControlTests.SetMenu(fixture.Root, 0);
                MenuControlTests.DestroyMenu(menu);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateOwnerThreadKeepsSelectionAndRestoreInWorker(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint control = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            control = host.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 1, 10);
            Native.SendText(control, combo ? 0x143u : 0x180u, 0, "first", 2, 1000, out _);
            Native.SendText(control, combo ? 0x143u : 0x180u, 0, "second", 2, 1000, out _);
            Native.ShowWindow(host.Root, 0);
        });
        var platform = new SessionPlatform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("second"), Value.Numeric(2 | 512)], default));
        Assert.Equal([(nint)77], platform.Restores);
        Assert.Equal((nint)1, Native.SendMessageW(control, combo ? 0x147u : 0x188u, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenRootSessionRestoresAfterMissingOrSelectedItem(bool found)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 1, 10);
        Native.SendText(control, 0x180, 0, "item", 2, 1000, out _);
        Native.ShowWindow(fixture.Root, 0);
        var platform = new SessionPlatform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(found ? 1 : 0), await session.InvokeAsync("CLKITEM",
            [id, Value.String(found ? "item" : "missing"), Value.Numeric(2 | 512)], default));
        Assert.Equal([(nint)77], platform.Restores);
        Assert.Equal((nint)(found ? 0 : -1), Native.SendMessageW(control, 0x188, 0, 0));
    }

    private sealed class SessionPlatform : OwnedButtonMessagePlatform
    {
        internal List<nint> Restores { get; } = [];

        internal int Captures
        {
            get; private set;
        }

        public override nint ForegroundWindow()
        {
            Captures++;
            return 77;
        }

        public override void Activate(nint root, CancellationToken token)
        {
            Assert.False(token.CanBeCanceled);
            Restores.Add(root);
        }
    }

    [Theory]
    [InlineData(true, false, 77, false)]
    [InlineData(false, false, 77, true)]
    [InlineData(true, true, 77, true)]
    [InlineData(false, false, 0, false)]
    public void SavesOnlyForHiddenOrIconicRoot(bool visible, bool iconic, int saved, bool restores)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var platform = new Platform { Visible = visible, Iconic = iconic, Saved = saved };
        Assert.True(NativeControlOperation.Execute(10, platform, _ => true, default));
        Assert.Equal(restores ? ["restore:77"] : Array.Empty<string>(), platform.Events);
        Assert.Equal(!visible || iconic, platform.Captured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureAndCancellationRestoreWithoutCancelledToken(bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var platform = new Platform();
        using var source = new CancellationTokenSource();
        bool Operation(CancellationToken token)
        {
            if (cancel)
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
            }

            throw new InvalidOperationException();
        }

        if (cancel)
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NativeControlOperation.Execute(10, platform, Operation, source.Token));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                NativeControlOperation.Execute(10, platform, Operation, source.Token));
        }

        Assert.Equal(["restore:77"], platform.Events);
    }

    [Fact]
    public async Task CallerTimeoutDoesNotRestoreBeforeNativeOperationReturns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var restored = new ManualResetEventSlim();
        var platform = new Platform { Restored = restored };
        Task<bool> pending = NativeControlOperation.RunAsync(10, platform, token =>
        {
            entered.Set();
            release.Wait();
            token.ThrowIfCancellationRequested();
            return true;
        }, default, TimeSpan.FromMilliseconds(200));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<TimeoutException>(() => pending);
            Assert.False(restored.IsSet);
        }
        finally
        {
            release.Set();
            Assert.True(restored.Wait(TimeSpan.FromSeconds(2)));
        }
    }

    private sealed class Platform : OwnedListSelectionPlatform
    {
        internal bool Visible
        {
            get; init;
        }
        internal bool Iconic
        {
            get; init;
        }
        internal nint Saved { get; init; } = 77;
        internal bool Captured
        {
            get; private set;
        }
        internal List<string> Events { get; } = [];
        internal ManualResetEventSlim? Restored
        {
            get; init;
        }

        internal Exception? RestoreFailure
        {
            get; init;
        }

        public override bool IsVisible(nint window) => Visible;

        public override bool IsIconic(nint window) => Iconic;

        public override uint CurrentThread() => 1;

        public override uint WindowThread(nint window) => 2;

        public override nint ForegroundWindow()
        {
            Captured = true;
            return Saved;
        }

        public override void Activate(nint root, CancellationToken token)
        {
            Assert.False(token.CanBeCanceled);
            Events.Add($"restore:{root}");
            Restored?.Set();
            if (RestoreFailure is not null)
            {
                throw RestoreFailure;
            }
        }
    }
}
