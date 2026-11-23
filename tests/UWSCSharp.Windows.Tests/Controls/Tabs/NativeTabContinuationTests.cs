using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTabContinuationTests
{
    private sealed class Platform : ToolbarOperationTests.RecordingPlatform
    {
        internal Action? Pumping;
        internal int Restores;

        public override void Sleep(int milliseconds, CancellationToken token) =>
            token.ThrowIfCancellationRequested();

        public override void Pump(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Pumping?.Invoke();
        }

        public override void Activate(nint root, CancellationToken token)
        {
            base.Activate(root, token);
            Restores++;
        }
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(0, true, 0)]
    [InlineData(0, false, 2048)]
    [InlineData(1, false, 0)]
    [InlineData(2, false, 0)]
    [InlineData(3, false, 0)]
    public async Task SelectionMismatchMovesToNextControlWithoutResettingMatchCount(
        int number, bool rejectSecond, int flags)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = TabControlTests.Create(fixture, 10, 0,
            "other", "target", "target", "unused");
        nint second = TabControlTests.Create(fixture, 60, 0,
            "other", "target", "unused");
        var platform = new Platform { Restoring = () => { } };
        List<nint> changed = [];
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -551)
            {
                changed.Add(notice.Window);
                if (notice.Window == first || rejectSecond)
                {
                    Native.SendMessageW(notice.Window, 0x130c, 0, 0);
                }
            }

            return 1;
        };
        List<int> firstReads = [];
        List<int> secondReads = [];
        List<nint> addresses = [];
        int rectangles = 0;
        using var firstHook = new ToolbarClickTextTests.Interceptor(first, (message, index, pointer) =>
        {
            if (message == 0x133c)
            {
                firstReads.Add((int)index);
                addresses.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12));
            }

            return null;
        });
        using var secondHook = new ToolbarClickTextTests.Interceptor(second, (message, index, pointer) =>
        {
            if (message == 0x133c)
            {
                secondReads.Add((int)index);
                addresses.Add(Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12));
            }

            if (message == 0x130a)
            {
                rectangles++;
                Marshal.WriteInt32(pointer, 10);
                Marshal.WriteInt32(pointer, 4, 20);
                return 0;
            }

            return null;
        });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        bool success = number is 0 or 3 && !rejectSecond;
        Assert.Equal(Value.Numeric(success ? 1 : 0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(8 | 512 | flags),
                Value.Numeric(1), Value.Numeric(number)], default));
        Assert.Equal(number is 0 or 1 ? [0, 1] : number == 2 ? [0, 1, 2]
            : new[] { 0, 1, 2, 3 }, firstReads);
        Assert.Equal(success || rejectSecond ? new[] { 0, 1 } : [0, 1, 2], secondReads);
        Assert.Equal(number == 3 ? new[] { second }
            : number == 0 ? [first, second] : [first], changed);
        Assert.All(addresses, address => Assert.Equal(platform.Buffers.Address, address));
        Assert.Equal(flags == 0 ? 0 : 1, rectangles);
        Assert.Equal(flags == 0 ? 0 : 2, platform.Posts.Count);
        Assert.All(platform.Posts, post => Assert.Equal(second, post.Target));
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Equal(1, platform.Restores);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task NextControlClearsTextThenRetainsPartialWritesWithinCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = TabControlTests.Create(fixture, 10, 0, "other", "target");
        nint second = TabControlTests.Create(fixture, 60, 0, "unused", "unused");
        var platform = new Platform { Restoring = () => { } };
        List<nint> changed = [];
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -551)
            {
                changed.Add(notice.Window);
                if (notice.Window == first)
                {
                    Native.SendMessageW(first, 0x130c, 0, 0);
                }
            }

            return 1;
        };
        List<int> reads = [];
        using var hook = new ToolbarClickTextTests.Interceptor(second, (message, index, pointer) =>
        {
            if (message != 0x133c)
            {
                return null;
            }

            reads.Add((int)index);
            nint text = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 16 : 12);
            Assert.Equal(platform.Buffers.Address, text);
            char[] value = (index == 0 ? "ta" : "rget\0").ToCharArray();
            Marshal.Copy(value, 0, text + (index == 0 ? 0 : 4), value.Length);
            return 0;
        });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(8 | 512),
                Value.Numeric(1), Value.Numeric(0)], default));
        Assert.Equal(new[] { 0, 1 }, reads);
        Assert.Equal(new[] { first, second }, changed);
        Assert.Equal(1, Native.SendMessageW(second, 0x130b, 0, 0));
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Equal(1, platform.Restores);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("throw")]
    public async Task WaitFailureDoesNotContinueToNextControl(string failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = TabControlTests.Create(fixture, 10, 0, "other", "target");
        nint second = TabControlTests.Create(fixture, 60, 0, "target");
        using var cancel = new CancellationTokenSource();
        var platform = new Platform { Restoring = () => { } };
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -551)
            {
                Native.SendMessageW(first, 0x130c, 0, 0);
                if (failure == "cancel")
                {
                    cancel.Cancel();
                }
                else
                {
                    platform.Pumping = () => throw new InvalidOperationException("待機失敗");
                }
            }

            return 1;
        };
        int laterReads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(second, (message, _, _) =>
        {
            laterReads += message == 0x1304 ? 1 : 0;
            return null;
        });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("target"), Value.Numeric(8 | 512),
            Value.Numeric(1), Value.Numeric(0)];
        if (failure == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal(0, laterReads);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Equal(1, platform.Restores);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task SelectionMismatchContinuesToLaterNativeTypeWithSharedOwner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "other", "target");
        nint toolbar = ToolbarControlTests.Create(fixture, 60);
        ToolbarControlTests.Add(toolbar, "target", 701, state: 5);
        fixture.Notify = (_, pointer) =>
        {
            if (Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code == -551)
            {
                Native.SendMessageW(tab, 0x130c, 0, 0);
            }

            return 1;
        };
        var platform = new Platform { Restoring = () => { } };
        int buttonReads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message == 0x417)
            {
                buttonReads++;
                Assert.Equal(platform.Buffers.Address, pointer);
            }

            return null;
        });
        await using IWindowsAutomationSession session = ToolbarOperationTests.Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(8 | 64 | 512)], default));
        Assert.Equal(1, buttonReads);
        Assert.Empty(platform.Posts);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Equal(1, platform.Restores);
        Assert.Null(fixture.CallbackFailure);
    }
}
