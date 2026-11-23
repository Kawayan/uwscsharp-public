using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class DirectInputTests
{
    internal sealed class Backend : IDirectInputBackend
    {
        internal List<(nint Window, uint Message,
            uint WParam, uint LParam)> Messages
        {
            get;
        } = [];

        internal List<(int Shift, int Control, int Alt,
            bool IncludeAlt)> Modifiers
        {
            get;
        } = [];

        internal Native.Point? SelectedClick;
        internal short KeyState;
        internal nint Target = 20;
        internal nint InvalidWindow;
        internal uint? FailMessage;
        internal bool FailAfterSend;

        public bool IsWindow(nint window) =>
            window != 0 && window != InvalidWindow;

        public uint MapVirtualKey(uint key) => key;

        public short GetKeyState(int key) => KeyState;

        public DirectTarget SelectMouse(nint root,
            Native.Point position) => new(Target, position);

        public nint SelectKeyboard(nint root, Native.Point? lastClick)
        {
            SelectedClick = lastClick;
            return Target;
        }

        public void Post(nint window, uint message,
            uint wParam, uint lParam)
        {
            if (message == FailMessage)
            {
                throw new IOException("送信失敗");
            }

            Messages.Add((window, message, wParam, lParam));
        }

        public void WithModifiers(nint window,
            DirectKeyboardState state, bool includeAlt, Action send)
        {
            Modifiers.Add((state.Shift, state.Control,
                state.Alt, includeAlt));
            send();
            if (FailAfterSend)
            {
                throw new IOException("補助状態解除失敗");
            }
        }
    }

    [Fact]
    public async Task KeyboardUsesDirectMessagesAndModifierSlots()
    {
        var backend = new Backend();
        var session = new DirectInputSession(backend);
        await session.KeyAsync(10, 0xa1, 1, default);
        await session.KeyAsync(10, 18, 1, default);
        Assert.Empty(backend.Messages);
        await session.KeyAsync(10, 0x3042, 0, default);
        Assert.Equal([(20, 0x100u, 0x3042u, 0x00420001u),
            (20, 0x101u, 0x3042u, 0xc0420001u)],
            backend.Messages.Select(message =>
                ((int)message.Window, message.Message,
                    message.WParam, message.LParam)));
        Assert.Equal((0xa1, 0, 18, true),
            Assert.Single(backend.Modifiers));
        await session.KeyAsync(10, 16, 2, default);
        await session.KeyAsync(10, 18, 2, default);
        await session.KeyAsync(10, 65, 0, default);
        Assert.Equal((0, 0, 0, true), backend.Modifiers[1]);
    }

    [Theory]
    [InlineData(0, 0x201, 0x202, 1)]
    [InlineData(1, 0x204, 0x205, 2)]
    [InlineData(2, 0x207, 0x208, 16)]
    public async Task MouseClickUsesThreeMovesAndPreviousButtonState(
        int button, uint down, uint up, uint bit)
    {
        var backend = new Backend();
        var session = new DirectInputSession(backend);
        await session.MouseAsync(10, button, 0, new(0, -2), default);
        Assert.Equal([0x200u, 0x200u, 0x200u, down,
            0x200u, 0x200u, 0x200u, up],
            backend.Messages.Select(message => message.Message));
        Assert.Equal(DirectInputSession.Pack(1, -1),
            backend.Messages[0].LParam);
        Assert.Equal(0u, backend.Messages[1].LParam);
        Assert.Equal(DirectInputSession.Pack(0, -2),
            backend.Messages[2].LParam);
        Assert.All(backend.Messages.Take(4),
            message => Assert.Equal(0u, message.WParam));
        Assert.All(backend.Messages.Skip(4),
            message => Assert.Equal(bit, message.WParam));
        List<Exception> errors = [];
        session.Release(errors);
        Assert.Empty(errors);
        Assert.Equal(8, backend.Messages.Count);
    }

    [Theory]
    [InlineData(-1, 0xff880004u)]
    [InlineData(2, 0x00f00004u)]
    public async Task WheelPacksSignedDeltaAndShift(int notches,
        uint expected)
    {
        var backend = new Backend();
        var session = new DirectInputSession(backend);
        await session.KeyAsync(10, 16, 1, default);
        await session.MouseAsync(10, 5, notches, new(12, 13), default);
        Assert.Equal(0x20au, backend.Messages[^1].Message);
        Assert.Equal(expected, backend.Messages[^1].WParam);
        Assert.False(Assert.Single(backend.Modifiers).IncludeAlt);
    }

    [Fact]
    public async Task KeyboardFollowsLastLeftClickOnlyOnSameRoot()
    {
        var backend = new Backend();
        var session = new DirectInputSession(backend);
        await session.MouseAsync(10, 0, 1, new(12, 13), default);
        await session.KeyAsync(10, 65, 0, default);
        Assert.Equal(12, backend.SelectedClick!.Value.X);
        Assert.Equal(13, backend.SelectedClick.Value.Y);
        await session.KeyAsync(11, 65, 0, default);
        Assert.Null(backend.SelectedClick);
    }

    [Fact]
    public async Task ReleaseUsesOriginalTargetsAndContinuesAfterFailure()
    {
        var backend = new Backend();
        var session = new DirectInputSession(backend);
        await session.KeyAsync(10, 65, 1, default);
        await session.MouseAsync(10, 0, 1, new(3, 4), default);
        backend.Target = 30;
        await session.KeyAsync(11, 66, 1, default);
        backend.FailMessage = 0x101;
        List<Exception> errors = [];
        session.Release(errors);
        Assert.Equal(2, errors.Count);
        Assert.Equal((20, 0x202u, 1u, 0x00040003u),
            ((int)backend.Messages[^1].Window,
                backend.Messages[^1].Message,
                backend.Messages[^1].WParam,
                backend.Messages[^1].LParam));
        int count = backend.Messages.Count;
        session.Release(errors);
        Assert.Equal(count, backend.Messages.Count);
    }

    [Fact]
    public async Task CancellationDuringModifierWaitSendsNoKey()
    {
        var backend = new Backend
        {
            KeyState = unchecked((short)0x8000)
        };
        var session = new DirectInputSession(backend);
        using var cancellation = new CancellationTokenSource(40);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.KeyAsync(10, 65, 0,
                cancellation.Token));
        Assert.Empty(backend.Messages);
        backend.KeyState = 0x000f;
        await session.KeyAsync(10, 65, 0, default);
        Assert.Equal(2, backend.Messages.Count);
    }

    [Fact]
    public async Task CancelledMouseClickReleasesSuccessfulDown()
    {
        var backend = new Backend();
        var session = new DirectInputSession(backend);
        await session.MouseAsync(10, 0, 1, new(3, 4), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.MouseAsync(10, 0, 2,
                new(3, 4), cancellation.Token));
        List<Exception> errors = [];
        session.Release(errors);
        Assert.Empty(errors);
        Assert.Equal(0x202u, backend.Messages[^1].Message);
    }

    [Fact]
    public async Task FailureAfterSuccessfulDownStillTracksRelease()
    {
        var backend = new Backend { FailAfterSend = true };
        var session = new DirectInputSession(backend);
        await Assert.ThrowsAsync<IOException>(async () =>
            await session.KeyAsync(10, 65, 1, default));
        List<Exception> errors = [];
        session.Release(errors);
        Assert.Empty(errors);
        Assert.Equal(0x101u, backend.Messages[^1].Message);
    }

    [Fact]
    public async Task InvalidRootAndExpiredTargetsDoNotRedirectInput()
    {
        var backend = new Backend();
        var session = new DirectInputSession(backend);
        await session.KeyAsync(0, 65, 0, default);
        await session.MouseAsync(0, 0, 0, new(1, 2), default);
        Assert.Empty(backend.Messages);
        await session.KeyAsync(10, 65, 1, default);
        backend.InvalidWindow = backend.Target;
        List<Exception> errors = [];
        session.Release(errors);
        Assert.Empty(errors);
        Assert.Single(backend.Messages);
    }

    [Fact]
    public void KeyboardStateClearsModifierGroupsAndPreservesOtherKeys()
    {
        byte[] keyboard = [.. Enumerable.Repeat((byte)1, 256)];
        var state = new DirectKeyboardState();
        state.ApplyModifier(0xa1, false);
        state.ApplyModifier(17, false);
        state.ApplyModifier(18, false);
        NativeDirectInputBackend.ApplyModifiers(keyboard, state, false);
        Assert.Equal(0x80, keyboard[16]);
        Assert.Equal(0x80, keyboard[0xa1]);
        Assert.Equal(0, keyboard[0xa0]);
        Assert.Equal(0x80, keyboard[17]);
        Assert.Equal(1, keyboard[18]);
        Assert.Equal(1, keyboard[65]);
        NativeDirectInputBackend.ClearModifiers(keyboard, true);
        foreach (int key in new[] { 16, 17, 18,
            0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5 })
        {
            Assert.Equal(0, keyboard[key]);
        }

        Assert.Equal(1, keyboard[65]);
    }
}
