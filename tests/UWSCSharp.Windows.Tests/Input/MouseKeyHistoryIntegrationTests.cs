using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class MouseKeyHistoryIntegrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PollPreservesReleasedClickForOneRead(int physicalKey)
    {
        var backend = new Backend();
        backend.States[physicalKey] = 1;
        await using IWindowsAutomationSession session =
            Automation(backend).CreateSession();
        Assert.True(session.RequiresInputPolling);

        session.PollInput(default);
        Assert.Equal([1, 2, 4], backend.AsyncReads);
        // 偽APIも、GetAsyncKeyStateと同様に直近押下のbitを消費します。
        Assert.Equal(0, backend.States[physicalKey]);
        backend.States[physicalKey] = 0;

        Assert.Equal(Value.Numeric(1), await ReadAsync(session, physicalKey));
        Assert.Equal(Value.Numeric(0), await ReadAsync(session, physicalKey));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task HeldReadConsumesHistoryBeforeRelease(int physicalKey)
    {
        var backend = new Backend();
        backend.States[physicalKey] = short.MinValue;
        await using IWindowsAutomationSession session =
            Automation(backend).CreateSession();

        session.PollInput(default);
        Assert.Equal(Value.Numeric(1), await ReadAsync(session, physicalKey));
        Assert.Equal(Value.Numeric(1), await ReadAsync(session, physicalKey));
        backend.States[physicalKey] = 0;

        Assert.Equal(Value.Numeric(0), await ReadAsync(session, physicalKey));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task AppliesMouseSwapOnceWhenConsumingHistory(
        int logicalKey, int physicalKey)
    {
        var backend = new Backend();
        backend.States[physicalKey] = 1;
        await using IWindowsAutomationSession session =
            Automation(backend, swapped: true).CreateSession();

        session.PollInput(default);
        Assert.Equal(Value.Numeric(0), await ReadAsync(session, physicalKey));
        Assert.Equal(Value.Numeric(1), await ReadAsync(session, logicalKey));
        Assert.Equal(Value.Numeric(0), await ReadAsync(session, logicalKey));
        Assert.Equal([1, 2, 4, logicalKey, physicalKey, physicalKey],
            backend.AsyncReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiddleButtonDoesNotChangeWhenMouseButtonsAreSwapped(
        bool swapped)
    {
        var backend = new Backend();
        backend.States[4] = 1;
        await using IWindowsAutomationSession session =
            Automation(backend, swapped).CreateSession();

        session.PollInput(default);

        Assert.Equal(Value.Numeric(1), await ReadAsync(session, 4));
        Assert.Equal(Value.Numeric(0), await ReadAsync(session, 4));
        Assert.Equal(4, backend.AsyncReads[^1]);
    }

    [Fact]
    public async Task OtherKeysAndToggleAndImeDoNotConsumeMouseHistory()
    {
        var backend = new Backend
        {
            ToggleState = 1,
            ImeState = true
        };
        int[] physicalKeys = [1, 2, 4];
        foreach (int key in physicalKeys)
        {
            backend.States[key] = 1;
        }

        await using IWindowsAutomationSession session =
            Automation(backend).CreateSession();
        session.PollInput(default);

        Assert.Equal(Value.Numeric(0), await ReadAsync(session, 65));
        Assert.Equal(Value.Numeric(1), await ReadAsync(session, 10000));
        Assert.Equal(Value.Numeric(1), await ReadAsync(session, 10004));
        Assert.Equal([144], backend.ToggleReads);
        Assert.Equal(1, backend.ImeReads);
        foreach (int key in physicalKeys)
        {
            Assert.Equal(Value.Numeric(1), await ReadAsync(session, key));
            Assert.Equal(Value.Numeric(0), await ReadAsync(session, key));
        }
    }

    [Fact]
    public async Task SessionsDoNotSharePendingMouseHistory()
    {
        var backend = new Backend();
        backend.States[1] = 1;
        WindowsAutomation automation = Automation(backend);
        await using IWindowsAutomationSession first = automation.CreateSession();
        await using IWindowsAutomationSession second = automation.CreateSession();

        first.PollInput(default);

        Assert.Equal(Value.Numeric(0), await ReadAsync(second, 1));
        Assert.Equal(Value.Numeric(1), await ReadAsync(first, 1));
        Assert.Equal(Value.Numeric(0), await ReadAsync(first, 1));
    }

    [Fact]
    public async Task DisposedSessionStopsPollingAndDoesNotLeakIntoNextSession()
    {
        var backend = new Backend();
        backend.States[1] = 1;
        WindowsAutomation automation = Automation(backend);
        await using IWindowsAutomationSession first = automation.CreateSession();
        first.PollInput(default);

        await first.DisposeAsync();
        int reads = backend.AsyncReads.Count;
        first.PollInput(default);
        Assert.Equal(reads, backend.AsyncReads.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await ReadAsync(first, 1));
        await using IWindowsAutomationSession second = automation.CreateSession();

        Assert.Equal(Value.Numeric(0), await ReadAsync(second, 1));
    }

    [Fact]
    public async Task CanceledPollDoesNotConsumeEarlierHistory()
    {
        var backend = new Backend();
        backend.States[2] = 1;
        await using IWindowsAutomationSession session =
            Automation(backend).CreateSession();
        session.PollInput(default);
        int reads = backend.AsyncReads.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(
            () => session.PollInput(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(reads, backend.AsyncReads.Count);
        Assert.Equal(Value.Numeric(1), await ReadAsync(session, 2));
    }

    [Fact]
    public async Task CanceledReadDoesNotConsumePendingMouseHistory()
    {
        var backend = new Backend();
        backend.States[1] = 1;
        await using IWindowsAutomationSession session =
            Automation(backend).CreateSession();
        session.PollInput(default);
        using var cancellation = new CancellationTokenSource();
        backend.AfterAsyncRead = _ => cancellation.Cancel();

        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await ReadAsync(session, 1, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        backend.AfterAsyncRead = null;
        Assert.Equal(Value.Numeric(1), await ReadAsync(session, 1));
        Assert.Equal(Value.Numeric(0), await ReadAsync(session, 1));
    }

    private static WindowsAutomation Automation(IKeyStateBackend backend,
        bool swapped = false) => new(
            _ => throw new InvalidOperationException("入力送信は禁止です。"),
            swappedMouseButtons: swapped, keyStateBackend: backend);

    private static ValueTask<Value> ReadAsync(
        IWindowsAutomationSession session, int key,
        CancellationToken cancellationToken = default) =>
        session.InvokeAsync("GETKEYSTATE", [Value.Numeric(key)],
            cancellationToken);

    private sealed class Backend : IKeyStateBackend
    {
        internal Dictionary<int, short> States
        {
            get;
        } = [];

        internal List<int> AsyncReads
        {
            get;
        } = [];

        internal List<int> ToggleReads
        {
            get;
        } = [];

        internal short ToggleState;
        internal bool ImeState;
        internal int ImeReads;
        internal Action<int>? AfterAsyncRead;

        public short ReadAsync(int key)
        {
            AsyncReads.Add(key);
            short state = States.GetValueOrDefault(key);
            States[key] = unchecked((short)(state & 0x8000));
            AfterAsyncRead?.Invoke(key);
            return state;
        }

        public short ReadToggle(int key)
        {
            ToggleReads.Add(key);
            return ToggleState;
        }

        public bool ReadIme(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ImeReads++;
            return ImeState;
        }
    }
}
