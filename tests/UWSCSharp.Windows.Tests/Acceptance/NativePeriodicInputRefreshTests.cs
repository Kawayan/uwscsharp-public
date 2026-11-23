using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativePeriodicInputRefreshTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PeriodicSnapshotsFollowPopupVisibilityAndDestruction()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var receiver = new Receiver();
        ButtonControlTests.Fixture? controls = null;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            host =>
            {
                controls = host;
                host.ObserveInput(host.Root, receiver.Receive);
            }, dpiAwarenessContext: -4);
        await UiInputDriver.ActivateAsync(fixture.Root);
        var backend = new Backend();
        ObservingSession? session = null;
        var owner = new SelectiveInputHookOwner(() =>
            session = new ObservingSession(fixture.Root, backend));
        var windows = new List<nint>();
        nint popup = 0;
        try
        {
            await owner.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Native.Point point = PhysicalPoint(fixture.Root, 30, 30);
            await receiver.SampleAsync(fixture, fixture.Root, point, 0);
            fixture.Invoke(() =>
            {
                using var dpi = new PhysicalDpiScope();
                for (int index = 0; index < 64; index++)
                {
                    nint window = CreateWindowExW(0, "STATIC", "一覧負荷",
                        0x80000000, 700 + index % 8 * 12,
                        250 + index / 8 * 12, 10, 10, fixture.Root, 0, 0, 0);
                    Assert.NotEqual(0, window);
                    windows.Add(window);
                    Native.ShowWindow(window, 4);
                }

                Native.Point origin = PhysicalPoint(fixture.Root, 20, 20);
                popup = CreateWindowExW(0, "STATIC", "周期更新popup",
                    0x80000000, origin.X, origin.Y, 120, 80, fixture.Root, 0, 0, 0);
                Assert.NotEqual(0, popup);
                Native.ShowWindow(popup, 4);
                controls!.ObserveInput(popup, receiver.Receive);
            });
            Assert.Equal(popup, WindowAt(point));
            session!.Observe(1, popup, point);
            await CheckSnapshot(1, popup, true, true);
            await receiver.SampleAsync(fixture, popup, point, 1);

            fixture.Invoke(() => Native.ShowWindow(popup, 0));
            Assert.Equal(fixture.Root, WindowAt(point));
            session.Observe(2, popup, point);
            await CheckSnapshot(2, fixture.Root, true, false);
            await receiver.SampleAsync(fixture, fixture.Root, point, 0);

            fixture.Invoke(() => Native.ShowWindow(popup, 4));
            Assert.Equal(popup, WindowAt(point));
            session.Observe(3, popup, point);
            await CheckSnapshot(3, popup, true, true);
            await receiver.SampleAsync(fixture, popup, point, 1);

            nint destroyed = popup;
            fixture.Invoke(() => Assert.True(Native.DestroyWindow(popup)));
            popup = 0;
            session.Observe(4, destroyed, point);
            await CheckSnapshot(4, fixture.Root, false, false);
            await receiver.SampleAsync(fixture, fixture.Root, point, 0);
            await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(owner.Failure);
            Assert.False(owner.IsAlive);
            Assert.Equal(0, backend.Active);
            await receiver.SampleAsync(fixture, fixture.Root, point, 1);
        }
        finally
        {
            try
            {
                await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                fixture.Invoke(() =>
                {
                    if (popup != 0)
                    {
                        Native.DestroyWindow(popup);
                    }

                    foreach (nint window in windows)
                    {
                        Assert.True(Native.DestroyWindow(window));
                    }
                });
            }
        }

        async Task CheckSnapshot(int generation, nint resolved, bool exists, bool visible)
        {
            Snapshot snapshot = await session!.NextSnapshotAsync(generation);
            Assert.Equal((long)resolved, snapshot.Resolved);
            Assert.Equal(exists, snapshot.Exists);
            Assert.Equal(visible, snapshot.Visible);
            Assert.Equal(session.OwnerThread, snapshot.Thread);
            Assert.Null(owner.Failure);
            output.WriteLine(JsonSerializer.Serialize(snapshot));
        }
    }

    [Fact]
    public async Task RefreshFailureSuspendsRealHookAndRetainsItUntilCleanupRetry()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var receiver = new Receiver();
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            host => host.ObserveInput(host.Root, receiver.Receive), dpiAwarenessContext: -4);
        await UiInputDriver.ActivateAsync(fixture.Root);
        var backend = new Backend();
        ObservingSession? session = null;
        var owner = new SelectiveInputHookOwner(() =>
            session = new ObservingSession(fixture.Root, backend));
        try
        {
            await owner.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Native.Point point = PhysicalPoint(fixture.Root, 30, 30);
            await receiver.SampleAsync(fixture, fixture.Root, point, 0);
            backend.FailRemoval = true;
            Volatile.Write(ref session!.FailRefresh, true);
            Assert.True(SpinWait.SpinUntil(() => owner.Failure is AggregateException, 5000));
            AggregateException error = Assert.IsType<AggregateException>(owner.Failure);
            Assert.Contains(session.Error, error.Flatten().InnerExceptions);
            Assert.True(owner.IsAlive);
            Assert.Equal(1, backend.Active);
            int refreshes = Volatile.Read(ref session.Refreshes);
            await receiver.SampleAsync(fixture, fixture.Root, point, 1);
            Assert.Equal(refreshes, Volatile.Read(ref session.Refreshes));
            backend.FailRemoval = false;
            await Assert.ThrowsAsync<AggregateException>(() =>
                owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(owner.IsAlive);
            Assert.Equal(0, backend.Active);
            await receiver.SampleAsync(fixture, fixture.Root, point, 1);
            output.WriteLine($"refreshes={refreshes} removals={backend.Removals} active={backend.Active}");
        }
        finally
        {
            backend.FailRemoval = false;
            try
            {
                await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException) when (!owner.IsAlive && backend.Active == 0)
            {
                // 検査済みの元の失敗は保持し、二度目の後始末でも回収だけを確認します。
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExplicitStopRetriesRealHookAndKeepsFailure(int failures)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var receiver = new Receiver();
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            host => host.ObserveInput(host.Root, receiver.Receive), dpiAwarenessContext: -4);
        var backend = new Backend();
        var owner = new SelectiveInputHookOwner(() =>
            new ObservingSession(fixture.Root, backend));
        try
        {
            await owner.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            Native.Point point = PhysicalPoint(fixture.Root, 30, 30);
            await receiver.SampleAsync(fixture, fixture.Root, point, 0);
            backend.RemovalFailures = failures;
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(
                () => owner.DisposeAsync().AsTask());
            Assert.Contains(error.Flatten().InnerExceptions,
                item => item is System.ComponentModel.Win32Exception native
                    && native.NativeErrorCode == 5);
            Assert.Equal(2, backend.Removals);
            if (failures == 2)
            {
                Assert.True(owner.IsAlive);
                Assert.Equal(1, backend.Active);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await receiver.SampleAsync(fixture, fixture.Root, point, 1);
                await Assert.ThrowsAsync<AggregateException>(
                    () => owner.DisposeAsync().AsTask());
                Assert.Equal(3, backend.Removals);
            }

            Assert.False(owner.IsAlive);
            Assert.Equal(0, backend.Active);
            await receiver.SampleAsync(fixture, fixture.Root, point, 1);
            Assert.NotNull(owner.Failure);
            output.WriteLine($"failures={failures} removals={backend.Removals} active={backend.Active}");
        }
        finally
        {
            backend.RemovalFailures = 0;
            try
            {
                await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException) when (!owner.IsAlive && backend.Active == 0)
            {
                // 元の解除失敗を保持したまま、後始末で残る登録がないことを確認します。
            }
        }
    }

    private sealed record Probe(int Generation, nint Window, Native.Point Point);

    private sealed record Snapshot(int Generation, long Resolved,
        bool Exists, bool Visible, int Thread, double CaptureMilliseconds);

    private sealed class ObservingSession : ISelectiveInputHookSession
    {
        private readonly NativeSelectiveInputHookSession inner;
        private readonly NativeSelectiveInputHookContext context;
        private readonly Channel<Snapshot> snapshots = Channel.CreateUnbounded<Snapshot>();
        private Probe? probe;
        internal readonly InvalidOperationException Error = new("一覧取得の注入失敗");
        internal readonly int OwnerThread = Environment.CurrentManagedThreadId;
        internal bool FailRefresh;
        internal int Refreshes;

        internal ObservingSession(nint target, Backend backend)
        {
            context = new NativeSelectiveInputHookContext(NativeSelectiveInputWindowTree.Capture());
            inner = new NativeSelectiveInputHookSession(target, SelectiveInputDevices.Mouse,
                backend, context);
        }

        public bool IsRegistered => inner.IsRegistered;

        public void Start() => inner.Start();

        public bool Pump() => inner.Pump();

        public void Wait(AutoResetEvent work, int milliseconds) => inner.Wait(work, milliseconds);

        public void Stop() => inner.Stop();

        public void RefreshTargets()
        {
            Interlocked.Increment(ref Refreshes);
            if (Volatile.Read(ref FailRefresh))
            {
                throw Error;
            }

            long started = Stopwatch.GetTimestamp();
            inner.RefreshTargets();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (Volatile.Read(ref probe) is { } current)
            {
                snapshots.Writer.TryWrite(new(current.Generation,
                    (long)context.ResolvePoint(current.Point.X, current.Point.Y),
                    Native.IsWindow(current.Window), Native.IsWindowVisible(current.Window),
                    Environment.CurrentManagedThreadId, elapsed));
            }
        }

        internal void Observe(int generation, nint window, Native.Point point) =>
            Volatile.Write(ref probe, new(generation, window, point));

        internal async Task<Snapshot> NextSnapshotAsync(int generation)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Snapshot snapshot = await snapshots.Reader.ReadAsync(timeout.Token);
                if (snapshot.Generation == generation)
                {
                    return snapshot;
                }
            }
        }
    }

    private sealed class Backend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend inner = new();
        internal volatile bool FailRemoval;
        internal int RemovalFailures;
        internal int Active;
        internal int Removals;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            (nint handle, int error) = inner.Install(type, procedure);
            if (handle != 0)
            {
                Interlocked.Increment(ref Active);
            }

            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            Interlocked.Increment(ref Removals);
            if (FailRemoval || Interlocked.Decrement(ref RemovalFailures) >= 0)
            {
                return (false, 5);
            }

            (bool success, int error) = inner.Remove(hook);
            if (success)
            {
                Interlocked.Decrement(ref Active);
            }

            return (success, error);
        }
    }

    private sealed class Receiver
    {
        private TaskCompletionSource sentinel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int downs;

        internal void Receive(uint message, nint key, nint data)
        {
            if (message is 0x201 or 0x203)
            {
                Interlocked.Increment(ref downs);
            }

            if (message == 0x101 && key == 0x86)
            {
                Volatile.Read(ref sentinel).TrySetResult();
            }
        }

        internal async Task SampleAsync(PumpedControlFixture fixture,
            nint window, Native.Point point, int expectedDowns)
        {
            // 完了通知のキーはrootで受け、マウスは物理位置の対象へ送ります。
            await UiInputDriver.ActivateAsync(fixture.Root,
                PhysicalPoint(fixture.Root, 5, 5));
            fixture.Invoke(() =>
            {
                SetFocus(fixture.Root);
                Interlocked.Exchange(ref downs, 0);
                Volatile.Write(ref sentinel,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            });
            Assert.True(Native.SetPhysicalCursorPos(point.X, point.Y));
            Assert.Equal(window, WindowAt(point));
            Native.Input[] inputs = [Mouse(2), Mouse(4), Key(0), Key(2)];
            Assert.Equal((uint)inputs.Length,
                Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>()));
            await Volatile.Read(ref sentinel).Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Invoke(() => { });
            Assert.Equal(expectedDowns, Volatile.Read(ref downs));
        }
    }

    private static Native.Point PhysicalPoint(nint window, int x, int y)
    {
        using var dpi = new PhysicalDpiScope();
        var point = new Native.Point(x, y);
        Assert.True(Native.ClientToScreen(window, ref point));
        return point;
    }

    private static nint WindowAt(Native.Point point)
    {
        using var dpi = new PhysicalDpiScope();
        return Native.WindowFromPoint(point);
    }

    private static Native.Input Mouse(uint flags) => new()
    {
        Type = 0,
        Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags } }
    };

    private static Native.Input Key(uint flags) => new()
    {
        Type = 1,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput { Vk = 0x86, Flags = flags, Extra = 1 }
        }
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint extendedStyle,
        string className, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint data);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
}
