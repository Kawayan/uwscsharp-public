using System.Collections.Concurrent;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// fake入力とfake COMでworker分離と共有資源の寿命を確認します。
/// </summary>
// 画面・入力・実COM serverを使わず、COM apartmentの初期化だけを行います。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowsSessionForkTests
{
    [Fact]
    public async Task WindowIdsAreSharedAndConcurrentRegistrationDoesNotDuplicate()
    {
        await using IWindowsAutomationSession root = Create();
        await using IWindowsAutomationSession first = root.Fork();
        await using IWindowsAutomationSession second = root.Fork();
        Task<(double First, double Second)>[] tasks = [.. Enumerable.Range(1, 64)
            .Select(index => Task.Run(async () =>
            {
                Value one = await first.InvokeAsync(BuiltinFunctionNames.HndToId,
                    [Value.Numeric(1000 + index)], default);
                Value two = await second.InvokeAsync(BuiltinFunctionNames.HndToId,
                    [Value.Numeric(1000 + index)], default);
                return (one.Number, two.Number);
            }))];
        (double First, double Second)[] values = await Task.WhenAll(tasks)
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.All(values, row => Assert.Equal(row.First, row.Second));
        Assert.Equal(64, values.Select(row => row.First).Distinct().Count());
        await first.DisposeAsync();
        Value stillOwned = await root.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric(1001)], default);
        Assert.Equal(values[0].First, stillOwned.Number);
    }

    [Fact]
    public async Task ForkStartsWithIndependentMouseHistory()
    {
        var keys = new Keys { State = 1 };
        await using IWindowsAutomationSession root = Create(keys: keys);
        root.PollInput(default);
        await using IWindowsAutomationSession worker = root.Fork();
        keys.State = 0;
        Assert.Equal(0, (await worker.InvokeAsync(BuiltinFunctionNames.GetKeyState,
            [Value.Numeric(1)], default)).Number);
        Assert.Equal(1, (await root.InvokeAsync(BuiltinFunctionNames.GetKeyState,
            [Value.Numeric(1)], default)).Number);
        Assert.Equal(0, (await root.InvokeAsync(BuiltinFunctionNames.GetKeyState,
            [Value.Numeric(1)], default)).Number);
    }

    [Fact]
    public async Task ForkCopiesOriginButKeepsHeldInputAndLaterOriginIndependent()
    {
        var direct = new Direct();
        await using IWindowsAutomationSession root = Create(direct: direct);
        double rootWindow = 4294967400d;
        double workerWindow = 4294967500d;
        await root.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [Value.Numeric(rootWindow), Value.Numeric(2)], default);
        await using IWindowsAutomationSession worker = root.Fork();
        await worker.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(66), Value.Numeric(1)], default);
        Assert.Equal((nint)(long)rootWindow, direct.Selected[^1]);
        await worker.InvokeAsync(BuiltinFunctionNames.MouseOrg,
            [Value.Numeric(workerWindow), Value.Numeric(2)], default);
        await worker.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(67), Value.Numeric(1)], default);
        Assert.Equal((nint)(long)workerWindow, direct.Selected[^1]);
        await root.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(65), Value.Numeric(1)], default);
        Assert.Equal((nint)(long)rootWindow, direct.Selected[^1]);
        Assert.False(root.CanPause);
        Assert.False(worker.CanPause);
        await worker.DisposeAsync();
        Assert.False(root.CanPause);
        Assert.Equal(new uint[] { 66, 67 }, direct.Posted
            .Where(item => item.Message == 0x101).Select(item => item.Key).Order());
        await root.DisposeAsync();
        Assert.Equal(new uint[] { 65, 66, 67 }, direct.Posted
            .Where(item => item.Message == 0x101).Select(item => item.Key).Order());
    }

    [Fact]
    public async Task ComReferencesAndStaAreSharedWhileSnapshotsAreIndependent()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime: runtime);
        Value target = await root.InvokeAsync(BuiltinFunctionNames.CreateOleObj,
            [Value.String("fake")], default);
        await root.InvokeAsync(BuiltinFunctionNames.GetOleItem, [target], default);
        Assert.Equal(2, root.AllOleItems.Count);
        await using IWindowsAutomationSession worker = root.Fork();
        Assert.Equal(root.AllOleItems, worker.AllOleItems);
        Assert.NotSame(root.AllOleItems, worker.AllOleItems);
        Value empty = await worker.InvokeMemberAsync(target, "empty", [], null, default);
        await worker.InvokeAsync(BuiltinFunctionNames.GetOleItem, [empty], default);
        Assert.Empty(worker.AllOleItems);
        Assert.Equal(2, root.AllOleItems.Count);
        // 列挙子は各列挙の終了時点で解放済みです。
        // workerの終了では、共有objectの追加解放がないことを確認します。
        int enumerationReleases = runtime.Released.Count;
        await worker.DisposeAsync();
        Assert.Equal(enumerationReleases, runtime.Released.Count);
        Assert.Equal(7, (await root.InvokeMemberAsync(target,
            "number", [], null, default)).Number);
        await root.DisposeAsync();
        Assert.Equal(enumerationReleases + 2, runtime.Released.Count);
        Assert.Single(runtime.Threads.Distinct());
        Assert.All(runtime.Apartments, value => Assert.Equal(ApartmentState.STA, value));
    }

    [Fact]
    public async Task RootDisposalWaitsForEveryForkAndOwnsInjectedBackend()
    {
        var touch = new Touch();
        IWindowsAutomationSession root = Create(touch: touch);
        IWindowsAutomationSession worker = root.Fork();
        IWindowsAutomationSession nested = worker.Fork();
        try
        {
            Task disposing = root.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, touch.Disposals);
            Assert.Same(disposing, root.DisposeAsync().AsTask());
            Assert.Throws<ObjectDisposedException>(() => root.Fork());
            Assert.Throws<ObjectDisposedException>(() => worker.Fork());
            await worker.DisposeAsync();
            Assert.False(disposing.IsCompleted);
            await nested.InvokeAsync(BuiltinFunctionNames.HndToId,
                [Value.Numeric(1234)], default);
            await nested.DisposeAsync();
            await disposing.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(1, touch.Disposals);
            await root.DisposeAsync();
            Assert.Equal(1, touch.Disposals);
        }
        finally
        {
            await nested.DisposeAsync();
            await worker.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentFirstComCallsUseOneStaAndRootRetainsOwnership()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime: runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        Task<Value>[] calls =
        [
            Task.Run(async () => await root.InvokeAsync(
                BuiltinFunctionNames.CreateOleObj, [Value.String("fake")], default)),
            Task.Run(async () => await worker.InvokeAsync(
                BuiltinFunctionNames.CreateOleObj, [Value.String("fake")], default))
        ];
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Single(runtime.Threads.Distinct());
        await worker.DisposeAsync();
        Assert.Empty(runtime.Released);
        await root.DisposeAsync();
        Assert.Equal(2, runtime.Released.Count);
    }

    private static IWindowsAutomationSession Create(Runtime? runtime = null,
        Keys? keys = null, Direct? direct = null, Touch? touch = null)
        => new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            directBackend: direct, comRuntime: runtime,
            touchBackend: touch, swappedMouseButtons: false,
            keyStateBackend: keys ?? new Keys()).CreateSession();

    private sealed class Keys : IKeyStateBackend
    {
        internal short State
        {
            get; set;
        }

        public short ReadAsync(int key) => State;

        public short ReadToggle(int key) => State;

        public bool ReadIme(CancellationToken token) => State != 0;
    }

    private sealed class Runtime : IComRuntime
    {
        private readonly List<object> root = [1d, "two"];
        internal List<object> Released { get; } = [];

        internal ConcurrentBag<int> Threads { get; } = [];

        internal ConcurrentBag<ApartmentState> Apartments { get; } = [];

        public object Create(string name)
        {
            Record();
            return root;
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name,
            object?[] arguments, bool assign)
        {
            Assert.Same(root, target);
            Record();
            return name == "empty" ? new List<object>() : 7d;
        }

        public void Release(object target)
        {
            Record();
            Released.Add(target);
        }

        private void Record()
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Apartments.Add(Thread.CurrentThread.GetApartmentState());
        }
    }

    private sealed class Direct : IDirectInputBackend
    {
        internal List<nint> Selected { get; } = [];

        internal List<(nint Window, uint Message, uint Key)> Posted { get; } = [];

        public bool IsWindow(nint window) => true;

        public uint MapVirtualKey(uint key) => key;

        public short GetKeyState(int key) => 0;

        public DirectTarget SelectMouse(nint root, Native.Point position)
            => new(root, position);

        public nint SelectKeyboard(nint root, Native.Point? lastClick)
        {
            Selected.Add(root);
            return root;
        }

        public void Post(nint window, uint message, uint wParam, uint lParam)
            => Posted.Add((window, message, wParam));

        public void WithModifiers(nint window, DirectKeyboardState state,
            bool includeAlt, Action send) => send();
    }

    private sealed class Touch : ITouchInputBackend
    {
        internal int Disposals
        {
            get; private set;
        }

        public void ValidatePosition(Native.Point position)
        {
        }

        public void Send(IReadOnlyList<TouchContact> contacts)
            => throw new InvalidOperationException("タッチ入力は禁止です。");

        public void Dispose() => Disposals++;
    }
}
