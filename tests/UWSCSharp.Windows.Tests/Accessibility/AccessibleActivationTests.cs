using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class AccessibleActivationTests
{
    [Theory]
    [InlineData("#32768")]
    [InlineData("basebar")]
    [InlineData("SomePopupClass")]
    [InlineData("UI TOOL WINDOW X")]
    [InlineData("MozillaDropShadowWindowClass")]
    public void EmptyPopupClassesSkipActivation(string label)
    {
        var backend = new Backend { Popup = true, Label = label };
        AccessibleActivation.Prepare(1, backend, default);
        Assert.Empty(backend.Events);
    }

    [Theory]
    [InlineData(false, "", 0)]
    [InlineData(true, "title", 0)]
    [InlineData(true, "", 3)]
    public void PopupExceptionsStillActivate(bool popup, string title, int parent)
    {
        var backend = new Backend
        {
            Popup = popup,
            Label = "POPUP",
            Caption = title,
            ParentWindow = parent,
        };
        AccessibleActivation.Prepare(1, backend, default);
        Assert.Equal(["attach:True", "foreground:1", "attach:False", "top:1"], backend.Events);
    }

    [Fact]
    public void InvalidOrAlreadyForegroundWindowDoesNotActivate()
    {
        var backend = new Backend { Valid = false };
        AccessibleActivation.Prepare(1, backend, default);
        backend.Valid = true;
        backend.Current = 1;
        AccessibleActivation.Prepare(1, backend, default);
        Assert.Empty(backend.Events);
    }

    [Fact]
    public void FailedActivationRetriesAndRestoresTimeout()
    {
        var backend = new Backend { Accept = false };
        AccessibleActivation.Prepare(1, backend, default);
        Assert.Equal(3, backend.Events.Count(value => value == "foreground:1"));
        Assert.Contains("foreground:9", backend.Events);
        Assert.Equal(3, backend.Events.Count(value => value == "yield"));
        Assert.Equal("timeout:42", backend.Events[^1]);
        Assert.Equal(backend.Events.Count(value => value == "attach:True"),
            backend.Events.Count(value => value == "attach:False"));
    }

    [Fact]
    public void ForegroundExceptionStillDetaches()
    {
        var backend = new Backend { FailForeground = true };
        Assert.Throws<InvalidOperationException>(() => AccessibleActivation.Prepare(1, backend, default));
        Assert.Equal(["attach:True", "foreground:1", "attach:False"], backend.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void YieldFailureOrCancellationRestoresTimeout(bool cancel)
    {
        using var source = new CancellationTokenSource();
        var backend = new Backend
        {
            Accept = false,
            DuringYield = () =>
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
            Assert.Throws<OperationCanceledException>(() => AccessibleActivation.Prepare(1, backend, source.Token));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => AccessibleActivation.Prepare(1, backend, source.Token));
        }
        Assert.Equal("timeout:42", backend.Events[^1]);
        Assert.Single(backend.Events, value => value == "yield");
    }

    [Fact]
    public void MdiActivatesAtMostThirdParent()
    {
        var backend = new Backend { Mdi = true, ParentWindow = 2 };
        AccessibleActivation.Prepare(1, backend, default);
        Assert.Contains("foreground:4", backend.Events);
        Assert.DoesNotContain("foreground:5", backend.Events);
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(0, true)]
    public async Task AccessibleReadRoutesBackBeforeReading(int flags, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new Backend();
        int worker = 0;
        backend.OnForeground = () => worker = Environment.CurrentManagedThreadId;
        Assert.Empty(await AccessibleItems.ReadAsync(0, 8388608 | flags,
            false, true, 0, default, backend));
        Assert.Equal(expected, backend.Events.Contains("foreground:0"));
        if (expected)
        {
            Assert.NotEqual(0, worker);
        }
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(0, true)]
    public async Task SessionPassesBackFlagToPreparation(int flags, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new Backend();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), accessibleActivation: backend)
            .CreateSession();
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM",
            [Value.Numeric(0), Value.Numeric(8388608 | flags)], default));
        Assert.Equal(expected, backend.Events.Contains("foreground:0"));
    }

    [Fact]
    public async Task PreparationFailurePublishesEmptySessionItems()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new Backend { FailForeground = true };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), accessibleActivation: backend)
            .CreateSession();
        await session.InvokeAsync("GETITEM",
            [Value.Numeric(0), Value.Numeric(8388608 | 512)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.InvokeAsync("GETITEM",
            [Value.Numeric(0), Value.Numeric(8388608)], default).AsTask());
        Assert.NotSame(snapshot, session.AllItems);
        Assert.Empty(session.AllItems);
        Assert.Equal("attach:False", backend.Events[^1]);
    }

    [Fact]
    public void InitialCancellationDoesNotCallBackend()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var backend = new Backend();
        Assert.Throws<OperationCanceledException>(() => AccessibleActivation.Prepare(1, backend, source.Token));
        Assert.Empty(backend.Events);
    }

    private sealed class Backend : IAccessibleActivationBackend
    {
        internal readonly List<string> Events = [];
        internal bool Valid = true;
        internal bool Popup;
        internal string Label = "Normal";
        internal string Caption = "";
        internal nint ParentWindow;
        internal nint Current = 8;
        internal bool Accept = true;
        internal bool FailForeground;
        internal bool Mdi;
        internal Action? DuringYield;
        internal Action? OnForeground;

        public bool IsWindow(nint window) => Valid;
        public bool AnyPopup() => Popup;
        public nint Parent(nint window) => Mdi ? window + 1 : ParentWindow;
        public nint Foreground() => Current;
        public string ClassName(nint window) => Label;
        public string Title(nint window) => Caption;
        public int ExtendedStyle(nint window) => Mdi ? 0x40 : 0;
        public uint ThreadId(nint window) => (uint)window;
        public void Attach(uint target, uint foreground, bool attach)
            => Events.Add($"attach:{attach}");
        public void SetForeground(nint window)
        {
            Events.Add($"foreground:{window}");
            OnForeground?.Invoke();
            if (FailForeground)
            {
                throw new InvalidOperationException();
            }

            if (Accept)
            {
                Current = window;
            }
        }

        public void BringToTop(nint window) => Events.Add($"top:{window}");
        public uint LockTimeout() => 42;
        public void SetLockTimeout(uint value) => Events.Add($"timeout:{value}");
        public nint ApplicationWindow() => 9;
        public void Yield(CancellationToken token)
        {
            Events.Add("yield");
            DuringYield?.Invoke();
            token.ThrowIfCancellationRequested();
        }
    }
}
