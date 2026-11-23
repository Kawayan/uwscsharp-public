using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class TouchInputTests
{
    private sealed class Backend : ITouchInputBackend
    {
        internal List<TouchContact[]> Frames { get; } = [];
        internal Func<int, Exception?> Failure { get; set; } = _ => null;
        internal Exception? DisposeFailure
        {
            get; set;
        }
        internal Native.Point? RejectedPosition
        {
            get; set;
        }
        internal int Attempts
        {
            get; private set;
        }
        internal bool Disposed
        {
            get; private set;
        }

        public void ValidatePosition(Native.Point position)
        {
            if (RejectedPosition is Native.Point rejected && position.Equals(rejected))
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }
        }

        public void Send(IReadOnlyList<TouchContact> contacts)
        {
            Exception? failure = Failure(++Attempts);
            if (failure is not null)
            {
                throw failure;
            }

            Frames.Add([.. contacts]);
        }

        public void Dispose()
        {
            Disposed = true;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }

    private static Task Immediate(int milliseconds,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InvalidDestinationDoesNotMoveContactsOrConsumeAnUpSlot()
    {
        var backend = new Backend { RejectedPosition = new(-1, 20) };
        var session = new TouchInputSession(backend);
        await session.InvokeAsync(1, new(10, 20), default, Immediate);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await session.InvokeAsync(2, new(-1, 20), default, Immediate));
        Assert.Single(backend.Frames);
        Assert.True(session.HasHeldInput);
        await session.InvokeAsync(2, new(10, 20), default, Immediate);
        Assert.False(session.HasHeldInput);
        Assert.Equal(TouchInputSession.Up, Assert.Single(backend.Frames[^1]).Flags);
    }

    [Fact]
    public async Task TwoContactsMoveTogetherAfterBothDestinationsArrive()
    {
        var backend = new Backend();
        var session = new TouchInputSession(backend);
        await session.InvokeAsync(1, new(10, 20), default, Immediate);
        await session.InvokeAsync(1, new(30, 40), default, Immediate);
        Assert.True(session.HasHeldInput);
        Assert.Equal([TouchInputSession.Update, TouchInputSession.Down],
            backend.Frames[1].Select(contact => contact.Flags));
        await session.InvokeAsync(2, new(12, 21), default, Immediate);
        Assert.Equal(2, backend.Frames.Count);
        Assert.True(session.HasHeldInput);
        await session.InvokeAsync(2, new(34, 42), default, Immediate);
        Assert.False(session.HasHeldInput);
        Assert.Equal(7, backend.Frames.Count);
        for (int index = 2; index < 6; index++)
        {
            Assert.Equal([1u, 2u], backend.Frames[index].Select(contact => contact.Id));
            Assert.All(backend.Frames[index], contact =>
                Assert.Equal(TouchInputSession.Update, contact.Flags));
        }

        TouchContact[] last = backend.Frames[^1];
        Assert.Equal(new Native.Point(12, 21), last[0].Position);
        Assert.Equal(new Native.Point(34, 42), last[1].Position);
        Assert.All(last, contact => Assert.Equal(TouchInputSession.Up, contact.Flags));
    }

    [Fact]
    public async Task ClickWaitsTwentyMillisecondsAndDoesNotRetainContact()
    {
        var backend = new Backend();
        var session = new TouchInputSession(backend);
        var waits = new List<int>();
        await session.InvokeAsync(0, new(10, 20), default,
            (milliseconds, _) =>
            {
                waits.Add(milliseconds);
                return Task.CompletedTask;
            });
        Assert.Contains(20, waits);
        Assert.Equal([TouchInputSession.Down, TouchInputSession.Up],
            backend.Frames.Select(frame => Assert.Single(frame).Flags));
        Assert.False(session.HasHeldInput);
    }

    [Fact]
    public async Task ThirdDownAndClickWhileHoldingLeaveExistingContactsIntact()
    {
        var backend = new Backend();
        var session = new TouchInputSession(backend);
        await session.InvokeAsync(1, new(10, 20), default, Immediate);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(0, new(20, 30), default, Immediate));
        await session.InvokeAsync(1, new(30, 40), default, Immediate);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(1, new(50, 60), default, Immediate));
        Assert.Equal(2, backend.Frames.Count);
        Assert.True(session.HasHeldInput);
        var errors = new List<Exception>();
        await session.ReleaseAsync(errors);
        Assert.Empty(errors);
        Assert.Equal(2, backend.Frames[^1].Length);
        Assert.All(backend.Frames[^1], contact =>
            Assert.Equal(TouchInputSession.Cancel, contact.Flags));
    }

    [Fact]
    public async Task UpWithoutDownDoesNotStartADevice()
    {
        var backend = new Backend();
        var session = new TouchInputSession(backend);
        await session.InvokeAsync(2, new(10, 20), default, Immediate);
        Assert.Empty(backend.Frames);
        Assert.False(session.HasHeldInput);
    }

    [Fact]
    public async Task FailedDownIsNotRecordedAndUnknownBackendFailureIsPreserved()
    {
        var failure = new ApplicationException("unknown backend");
        var backend = new Backend { Failure = _ => failure };
        var session = new TouchInputSession(backend);
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(async () =>
            await session.InvokeAsync(1, new(10, 20), default, Immediate)));
        Assert.False(session.HasHeldInput);
        var errors = new List<Exception>();
        await session.ReleaseAsync(errors);
        Assert.Empty(errors);
        Assert.Equal(1, backend.Attempts);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task CancelDuringInterpolationReleasesOnlyTheLastInjectedPosition()
    {
        var backend = new Backend();
        var session = new TouchInputSession(backend);
        using var cancellation = new CancellationTokenSource();
        await session.InvokeAsync(1, new(10, 20), default, Immediate);
        int updates = 0;
        backend.Failure = _ =>
        {
            if (++updates == 2)
            {
                cancellation.Cancel();
            }

            return null;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync(2, new(100, 200), cancellation.Token, Immediate));
        TouchContact previous = Assert.Single(backend.Frames[^1]);
        var errors = new List<Exception>();
        await session.ReleaseAsync(errors);
        Assert.Empty(errors);
        TouchContact release = Assert.Single(backend.Frames[^1]);
        Assert.Equal(previous.Position, release.Position);
        Assert.Equal(TouchInputSession.Cancel, release.Flags);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public async Task FailedUpCanBeRetriedWithoutLosingHeldContact()
    {
        var backend = new Backend();
        var session = new TouchInputSession(backend);
        await session.InvokeAsync(1, new(10, 20), default, Immediate);
        backend.Failure = count => count == 2 ? new IOException("up") : null;
        await Assert.ThrowsAsync<IOException>(async () =>
            await session.InvokeAsync(2, new(10, 20), default, Immediate));
        Assert.True(session.HasHeldInput);
        await session.InvokeAsync(2, new(10, 20), default, Immediate);
        Assert.False(session.HasHeldInput);
        Assert.Equal(TouchInputSession.Up, Assert.Single(backend.Frames[^1]).Flags);
    }

    [Fact]
    public async Task CleanupCollectsCancelAndDeviceFailuresAndRunsOnlyOnce()
    {
        var cancel = new IOException("cancel");
        var device = new ApplicationException("device");
        var backend = new Backend { DisposeFailure = device };
        var session = new TouchInputSession(backend);
        await session.InvokeAsync(1, new(10, 20), default, Immediate);
        backend.Failure = _ => cancel;
        var errors = new List<Exception>();
        await session.ReleaseAsync(errors);
        await session.ReleaseAsync(errors);
        Assert.Equal([cancel, device], errors);
        Assert.Equal(2, backend.Attempts);
        Assert.True(backend.Disposed);
        Assert.False(session.HasHeldInput);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync(1, new(10, 20), default, Immediate));
    }

    [Fact]
    public async Task WindowsSessionDisablesPauseAndDisposesTouchAfterOriginChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new Backend();
        var mouse = new List<Native.Input>();
        IWindowsAutomationSession session = new WindowsAutomation(
            mouse.Add, touchBackend: backend).CreateSession();
        await session.InvokeAsync("BTN", [Value.Numeric(7), Value.Numeric(1)], default);
        Assert.False(session.CanPause);
        await session.InvokeAsync("MOUSEORG", [Value.Numeric(999), Value.Numeric(2)], default);
        await session.InvokeAsync("BTN", [Value.Numeric(7)], default);
        Assert.Single(backend.Frames);
        await session.DisposeAsync();
        Assert.True(backend.Disposed);
        Assert.Equal(TouchInputSession.Cancel, Assert.Single(backend.Frames[^1]).Flags);
        Assert.Empty(mouse);
    }

    [Fact]
    public async Task WindowsCleanupTriesOtherInputsAfterTouchCancelFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var touchFailure = new IOException("touch cancel");
        var keyFailure = new ApplicationException("key up");
        var backend = new Backend();
        var inputs = new List<Native.Input>();
        IWindowsAutomationSession session = new WindowsAutomation(input =>
        {
            inputs.Add(input);
            if (input.Type == 1 && (input.Data.Keyboard.Flags & 2) != 0)
            {
                throw keyFailure;
            }
        }, touchBackend: backend, swappedMouseButtons: false).CreateSession();
        await session.InvokeAsync("BTN", [Value.Numeric(7), Value.Numeric(1)], default);
        await session.InvokeAsync("KBD", [Value.Numeric(65), Value.Numeric(1)], default);
        await session.InvokeAsync("BTN", [Value.Numeric(0), Value.Numeric(1)], default);
        backend.Failure = _ => touchFailure;
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(async () =>
            await session.DisposeAsync());
        Assert.Contains(touchFailure, failure.InnerExceptions);
        Assert.Contains(keyFailure, failure.InnerExceptions);
        Assert.Contains(inputs, input => input.Type == 0 && input.Data.Mouse.Flags == 4);
        Assert.True(backend.Disposed);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("EXITEXIT", false)]
    [InlineData("EXITEXIT 7", false)]
    [InlineData("DIM values[0]\nPRINT values[2]", true)]
    public async Task InterpreterReleasesTouchOnNaturalExitExplicitExitAndFailure(
        string tail, bool fails)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new Backend();
        var interpreter = new Interpreter(windows: new WindowsAutomation(
            _ => { }, touchBackend: backend));
        string source = "BTN(TOUCH,DOWN,10,20)\n" + tail;
        if (fails)
        {
            await Assert.ThrowsAsync<ScriptException>(() =>
                interpreter.RunAsync(source, _ => { }));
        }
        else
        {
            await interpreter.RunAsync(source, _ => { });
        }

        Assert.True(backend.Disposed);
        Assert.Equal(TouchInputSession.Cancel, Assert.Single(backend.Frames[^1]).Flags);
    }

    [Fact]
    public async Task InterpreterCancellationRunsFinallyBeforeReleasingTouch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var backend = new Backend();
        var output = new List<string>();
        backend.Failure = count =>
        {
            if (count == 1)
            {
                cancellation.Cancel();
            }

            return null;
        };
        var interpreter = new Interpreter(windows: new WindowsAutomation(
            _ => { }, touchBackend: backend));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interpreter.RunAsync(
            "OPTION OPTFINALLY\nTRY\nBTN(TOUCH,DOWN,10,20)\nWHILE TRUE\nWEND\nFINALLY\nPRINT \"cleanup\"\nENDTRY",
            text =>
            {
                Assert.False(backend.Disposed);
                output.Add(text);
            }, cancellation.Token));
        Assert.Equal(["cleanup"], output);
        Assert.True(backend.Disposed);
        Assert.Equal(TouchInputSession.Cancel, Assert.Single(backend.Frames[^1]).Flags);
    }

    [Fact]
    public async Task SwapSnapshotAppliesOnlyToPhysicalMouseAndKeepsHeldReleaseTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var inputs = new List<Native.Input>();
        var direct = new DirectInputTests.Backend();
        IWindowsAutomationSession session = new WindowsAutomation(
            inputs.Add, directBackend: direct, swappedMouseButtons: true).CreateSession();
        await session.InvokeAsync("BTN", [Value.Numeric(0), Value.Numeric(1)], default);
        await session.InvokeAsync("MOUSEORG", [Value.Numeric(999), Value.Numeric(2)], default);
        await session.InvokeAsync("BTN", [Value.Numeric(0)], default);
        Assert.Contains(direct.Messages, message => message.Message == 0x201);
        Assert.Contains(direct.Messages, message => message.Message == 0x202);
        Assert.False(session.CanPause);
        await session.DisposeAsync();
        Assert.Equal([8u, 16u], inputs.Select(input => input.Data.Mouse.Flags));
    }

    [Theory]
    [InlineData(0, false, 2, 4)]
    [InlineData(0, true, 8, 16)]
    [InlineData(1, false, 8, 16)]
    [InlineData(1, true, 2, 4)]
    [InlineData(2, true, 32, 64)]
    [InlineData(3, true, 128, 256)]
    [InlineData(4, true, 128, 256)]
    public async Task PhysicalSwapDoesNotChangeOtherButtons(
        int button, bool swapped, uint down, uint up)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var inputs = new List<Native.Input>();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            inputs.Add, swappedMouseButtons: swapped).CreateSession();
        await session.InvokeAsync("BTN", [Value.Numeric(button)], default);
        Assert.Equal([down, up], inputs.Select(input => input.Data.Mouse.Flags));
    }

    [Fact]
    public void DesktopBoundsAreCheckedBeforeAnyFrameIsSent()
    {
        var origin = new Native.Point(-1920, -100);
        NativeTouchInputBackend.ValidateContacts(
            [new(1, TouchInputSession.Down, origin),
                new(2, TouchInputSession.Down, new(1079, 899))],
            origin, 3000, 1000);
        foreach (Native.Point point in new Native.Point[]
            { new(-1921, 0), new(1080, 0), new(0, -101), new(0, 900) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NativeTouchInputBackend.ValidateContacts(
                    [new(1, TouchInputSession.Down, point)], origin, 3000, 1000));
        }

        Assert.Throws<ArgumentException>(() => NativeTouchInputBackend.ValidateContacts(
            [], origin, 3000, 1000));
    }

    [Fact]
    public void NativeCoordinatesUseTheVirtualScreenOriginAndCheckOverflow()
    {
        NativeTouchInputBackend.PointerTypeInfo frame = NativeTouchInputBackend.Encode(
            new(1, TouchInputSession.Down, new(-100, 200)), new(-1920, -100));
        Assert.Equal(new Native.Point(1820, 300), frame.Touch.Pointer.Pixel);
        Assert.Equal(1818, frame.Touch.Contact.Left);
        Assert.Throws<OverflowException>(() => NativeTouchInputBackend.Encode(
            new(1, TouchInputSession.Down, new(int.MaxValue, 0)), new(-1920, 0)));
    }

    [Fact]
    public void NativeLayoutMatchesWindowsX64AndUsesLegalPressure()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        Assert.Equal(96, Marshal.SizeOf<NativeTouchInputBackend.PointerInfo>());
        Assert.Equal(144, Marshal.SizeOf<NativeTouchInputBackend.TouchInfo>());
        Assert.Equal(152, Marshal.SizeOf<NativeTouchInputBackend.PointerTypeInfo>());
        Assert.Equal(8, Marshal.OffsetOf<NativeTouchInputBackend.PointerTypeInfo>("Touch").ToInt32());
        NativeTouchInputBackend.PointerTypeInfo frame = NativeTouchInputBackend.Encode(
            new(1, TouchInputSession.Down, new(10, 20)));
        Assert.Equal(2u, frame.Type);
        Assert.Equal(2u, frame.Touch.Pointer.Type);
        Assert.Equal(1024u, frame.Touch.Pressure);
        Assert.Equal(90u, frame.Touch.Orientation);
        Assert.Equal(7u, frame.Touch.Mask);
        Assert.Equal(8, frame.Touch.Contact.Left);
        Assert.Equal(22, frame.Touch.Contact.Bottom);
    }
}
