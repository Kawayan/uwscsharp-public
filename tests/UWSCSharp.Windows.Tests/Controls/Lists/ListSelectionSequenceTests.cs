using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ListSelectionSequenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CursorRestorationRunsAfterPreparationOrCloseException(bool close)
    {
        var backend = new Backend();
        if (close)
        {
            backend.Close = () => throw new InvalidOperationException();
        }
        else
        {
            backend.CursorPreparation = () => throw new InvalidOperationException();
        }

        Assert.Throws<InvalidOperationException>(() =>
            ListSelectionSequence.Execute(0, true, true, backend, default));
        Assert.Equal("restore-cursor", backend.Events[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnterRequiresOpenedDropdown(bool opens)
    {
        var backend = new Backend { Success = 1, Opens = opens };
        Assert.True(ListSelectionSequence.Execute(0, true, true, backend, default));
        Assert.Equal(opens, backend.Events.Contains("key:13"));
        Assert.DoesNotContain("close", backend.Events);
    }

    [Fact]
    public void ComboKeyFailureClosesAfterUnlock()
    {
        var backend = new Backend { Key = () => throw new InvalidOperationException() };
        Assert.Throws<InvalidOperationException>(() =>
            ListSelectionSequence.Execute(0, true, true, backend, default));
        Assert.Equal(["unlock", "close", "restore-cursor"], backend.Events.TakeLast(3));
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(false, 1, 2)]
    [InlineData(true, 0, 3)]
    [InlineData(true, 1, 1)]
    public void RetriesThroughVerificationAndUsesNeighborKeys(bool combo, int index, int success)
    {
        var backend = new Backend { Success = success };
        Assert.True(ListSelectionSequence.Execute(index, combo, false, backend, default));
        Assert.Equal(success, backend.Sets);
        Assert.Equal(["activate", "set", "activate", "prepare"], backend.Events.Take(4));
        Assert.Equal(success + 1, backend.Events.Count(value => value == "activate"));
        Assert.Equal(success - 1, backend.Events.Count(value => value == $"sleep:{(combo ? 100 : 50)}"));
        Assert.Equal(combo ? 1 : 0, backend.Events.Count(value => value == "key:13"));
        Assert.Equal(success, backend.Events.Count(value => value == "unlock"));
        int locked = backend.Events.IndexOf("lock");
        Assert.Equal(index == 0 ? "key:40" : "key:38", backend.Events[locked + 1]);
        Assert.Equal(index == 0 ? "key:38" : "key:40", backend.Events[locked + 2]);
        Assert.Equal("unlock", backend.Events[locked + 3]);
        Assert.Equal("yield:20", backend.Events[locked + 4]);
        Assert.Equal("prepare", backend.Events[locked + 5]);
        Assert.Equal("get", backend.Events[locked + 6]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustionUsesThreeAttemptsAndBackSuppressesActivation(bool combo)
    {
        var backend = new Backend();
        Assert.False(ListSelectionSequence.Execute(0, combo, true, backend, default));
        Assert.Equal(3, backend.Sets);
        Assert.DoesNotContain("activate", backend.Events);
        Assert.Equal(3, backend.Events.Count(value => value == $"sleep:{(combo ? 100 : 50)}"));
        Assert.Equal(6, backend.Events.Count(value => value == "yield:20"));
        Assert.DoesNotContain("key:13", backend.Events);
        Assert.Equal(combo, backend.Events.Contains("close"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyFailureOrCancellationUnlocksBeforeReturning(bool cancel)
    {
        using var source = new CancellationTokenSource();
        var backend = new Backend
        {
            Key = () =>
            {
                if (cancel)
                {
                    source.Cancel();
                }
                else
                {
                    throw new InvalidOperationException();
                }
            },
        };
        if (cancel)
        {
            Assert.Throws<OperationCanceledException>(() =>
                ListSelectionSequence.Execute(0, false, true, backend, source.Token));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                ListSelectionSequence.Execute(0, false, true, backend, source.Token));
        }

        Assert.Equal("unlock", backend.Events[^1]);
        Assert.DoesNotContain("get", backend.Events);
    }

    private sealed class Backend : IListSelectionBackend
    {
        internal readonly List<string> Events = [];
        internal int Success = int.MaxValue;
        internal int Sets;
        internal Action? Key;
        private int selected;

        internal bool Opens = true;

        public bool OpenCombo(CancellationToken token) => Opens;

        internal Action? Close;

        internal Action? CursorPreparation;

        public void CloseCombo()
        {
            Events.Add("close");
            Close?.Invoke();
        }

        public void PrepareComboCursor(CancellationToken token)
        {
            CursorPreparation?.Invoke();
        }

        public void RestoreComboCursor() => Events.Add("restore-cursor");

        public void ActivateRoot(CancellationToken token) => Events.Add("activate");
        public void PrepareRoot(CancellationToken token) => Events.Add("prepare");
        public void SetSelection(int index, CancellationToken token)
        {
            selected = index;
            Sets++;
            Events.Add("set");
        }

        public nint GetSelection(CancellationToken token)
        {
            Events.Add("get");
            return Sets >= Success ? selected : -1;
        }

        public void LockUpdates(bool locked) => Events.Add(locked ? "lock" : "unlock");
        public void PressKey(int key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Events.Add($"key:{key}");
            Key?.Invoke();
            token.ThrowIfCancellationRequested();
        }

        public void Sleep(int milliseconds, CancellationToken token) => Events.Add($"sleep:{milliseconds}");
        public void Yield(int iterations, CancellationToken token) => Events.Add($"yield:{iterations}");
    }
}
