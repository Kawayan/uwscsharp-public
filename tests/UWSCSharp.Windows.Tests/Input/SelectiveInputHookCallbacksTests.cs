using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputHookCallbacksTests
{
    [Theory]
    [InlineData(0, 9, true)]
    [InlineData(7, 7, true)]
    [InlineData(7, 9, false)]
    public void KeyboardUsesForegroundOnlyForSpecificTarget(int target, int foreground, bool blocked)
    {
        var context = new Context { Window = foreground };
        var callbacks = new SelectiveInputHookCallbacks(target, SelectiveInputDevices.All, context);
        using var data = new Buffer(false, 0);
        Assert.Equal(blocked ? 1 : 123, callbacks.Keyboard(0, 0x100, data.Pointer));
        Assert.Equal(target == 0 ? 0 : 1, context.Queries);
        Assert.Equal(blocked ? 0 : 1, context.Forwards.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtraOneBypassesWithoutTargetQuery(bool mouse)
    {
        var context = new Context();
        var callbacks = new SelectiveInputHookCallbacks(7, SelectiveInputDevices.All, context);
        using var data = new Buffer(mouse, 1);
        Assert.Equal(123, mouse ? callbacks.Mouse(0, 0x201, data.Pointer)
            : callbacks.Keyboard(0, 0x100, data.Pointer));
        Assert.Equal(0, context.Queries);
        Assert.Single(context.Forwards);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerSizedExtraDoesNotTruncateToOne(bool mouse)
    {
        var context = new Context();
        var callbacks = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.All, context);
        nuint extra = IntPtr.Size == 8 ? unchecked((nuint)0x100000001UL) : 2;
        using var data = new Buffer(mouse, extra);
        Assert.Equal(1, mouse ? callbacks.Mouse(0, 0x201, data.Pointer)
            : callbacks.Keyboard(0, 0x100, data.Pointer));
        Assert.Empty(context.Forwards);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void NonActionCodesNeverReadPointer(int code)
    {
        var context = new Context();
        var callbacks = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.All, context);
        Assert.Equal(123, callbacks.Keyboard(code, 0x100, 1));
        Assert.Equal(123, callbacks.Mouse(code, 0x201, 1));
        Assert.Equal((code, (nuint)0x100, (nint)1), context.Forwards[0]);
        Assert.Equal((code, (nuint)0x201, (nint)1), context.Forwards[1]);
        callbacks.ThrowIfFaulted();
    }

    [Theory]
    [InlineData(0, 0x200, true)]
    [InlineData(7, 0x200, false)]
    [InlineData(7, 0xa0, false)]
    public void MovementDoesNotResolvePoint(int target, uint message, bool blocked)
    {
        var context = new Context();
        var callbacks = new SelectiveInputHookCallbacks(target, SelectiveInputDevices.All, context);
        using var data = new Buffer(true, 0);
        Assert.Equal(blocked ? 1 : 123, callbacks.Mouse(0, message, data.Pointer));
        Assert.Equal(0, context.Queries);
    }

    [Fact]
    public void MouseCoordinatesAndLeftUpStateReachFilter()
    {
        var context = new Context { Window = 9 };
        var callbacks = new SelectiveInputHookCallbacks(7, SelectiveInputDevices.All, context);
        using var data = new Buffer(true, 0);
        Assert.Equal(123, callbacks.Mouse(0, 0x20a, data.Pointer));
        Assert.Equal((-20, 37), context.Point);
        context.Window = 7;
        Assert.Equal(123, callbacks.Mouse(0, 0x202, data.Pointer));
        Assert.Equal(1, callbacks.Mouse(0, 0x202, data.Pointer));
        Assert.Equal(3, context.Queries);
    }

    [Fact]
    public void DisabledDeviceNeverReadsPointer()
    {
        var context = new Context();
        var keyboard = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.Keyboard, context);
        var mouse = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.Mouse, context);
        Assert.Equal(123, keyboard.Mouse(0, 0x201, 1));
        Assert.Equal(123, mouse.Keyboard(0, 0x100, 1));
        keyboard.ThrowIfFaulted();
        mouse.ThrowIfFaulted();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryFailureIsRetainedAndBothStreamsPassAfterwards(bool mouse)
    {
        var expected = new InvalidOperationException("対象取得の失敗");
        var context = new Context { QueryFailure = expected };
        var callbacks = new SelectiveInputHookCallbacks(7, SelectiveInputDevices.All, context);
        using var data = new Buffer(mouse, 0);
        Assert.Equal(123, mouse ? callbacks.Mouse(0, 0x201, data.Pointer)
            : callbacks.Keyboard(0, 0x100, data.Pointer));
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(callbacks.ThrowIfFaulted));
        Assert.Equal(123, callbacks.Keyboard(0, 0x100, 1));
        Assert.Equal(123, callbacks.Mouse(0, 0x201, 1));
        Assert.Equal(1, context.Queries);
    }

    [Fact]
    public void SuspensionPassesBothStreamsWithoutReadingData()
    {
        var context = new Context();
        var callbacks = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.All, context);
        callbacks.Suspend();
        Assert.Equal(123, callbacks.Keyboard(0, 0x100, 1));
        Assert.Equal(123, callbacks.Mouse(0, 0x201, 1));
        Assert.Equal(0, context.Queries);
        callbacks.ThrowIfFaulted();
    }

    [Fact]
    public void ForwardFailureNeverEscapesCallback()
    {
        var expected = new InvalidOperationException("転送の失敗");
        var context = new Context { ForwardFailure = expected };
        var callbacks = new SelectiveInputHookCallbacks(0, SelectiveInputDevices.All, context);
        Assert.Equal(0, callbacks.Keyboard(-1, 0x100, 1));
        Assert.Equal(0, callbacks.Mouse(-1, 0x201, 1));
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(callbacks.ThrowIfFaulted));
    }

    private sealed class Buffer : IDisposable
    {
        internal nint Pointer { get; } = Marshal.AllocHGlobal(32);

        internal Buffer(bool mouse, nuint extra)
        {
            Marshal.Copy(new byte[32], 0, Pointer, 32);
            Marshal.WriteInt32(Pointer, 0, mouse ? -20 : 0x87);
            Marshal.WriteInt32(Pointer, 4, mouse ? 37 : 0);
            int extraOffset = mouse ? (IntPtr.Size == 8 ? 24 : 20) : 16;
            Marshal.WriteIntPtr(Pointer, extraOffset, unchecked((nint)extra));
        }

        /// <summary>
        /// テスト専用の通知データ領域を解放します。
        /// </summary>
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    private sealed class Context : ISelectiveInputHookContext
    {
        internal nint Window;
        internal int Queries;
        internal (int X, int Y) Point;
        internal Exception? QueryFailure;
        internal Exception? ForwardFailure;
        internal List<(int Code, nuint Message, nint Data)> Forwards { get; } = [];

        public nint Foreground()
        {
            Queries++;
            if (QueryFailure is not null)
            {
                throw QueryFailure;
            }

            return Window;
        }

        public nint ResolvePoint(int x, int y)
        {
            Point = (x, y);
            return Foreground();
        }

        public nint Next(int code, nuint message, nint data)
        {
            Forwards.Add((code, message, data));
            if (ForwardFailure is not null)
            {
                throw ForwardFailure;
            }

            return 123;
        }
    }
}
