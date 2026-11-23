using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ToolbarOperationTests
{
    internal class RecordingPlatform : OwnedListSelectionPlatform
    {
        internal List<(nint Target, uint Message, nint Parameter, nint Data)> Posts { get; } = [];
        internal List<string> Events { get; } = [];
        internal ToolbarClickTextTests.BufferPlatform Buffers { get; } = new();
        internal Action<uint>? Posting;
        internal Action? Restoring;

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) => new(root, Buffers);

        public override bool IsVisible(nint root) => Restoring is null && base.IsVisible(root);

        public override nint ForegroundWindow() => Restoring is null ? base.ForegroundWindow() : 77;

        public override void Activate(nint root, CancellationToken token)
        {
            Assert.True(Buffers.Released);
            Assert.True(Buffers.Closed);
            Restoring?.Invoke();
        }

        public override void Priority(uint value) => Events.Add("priority" + value);

        public override void Post(nint target, uint message, nint parameter, nint data)
        {
            Posts.Add((target, message, parameter, data));
            Events.Add("post" + message);
            Posting?.Invoke(message);
        }
    }

    internal static IWindowsAutomationSession Session(RecordingPlatform platform) =>
        new WindowsAutomation(_ => throw new InvalidOperationException(),
            listSelectionPlatform: platform).CreateSession();

    [Theory]
    [InlineData(0, 4, true)]
    [InlineData(2, 5, false)]
    [InlineData(0, 6, false)]
    [InlineData(6, 7, false)]
    [InlineData(8, 4, true)]
    [InlineData(128, 4, true)]
    [InlineData(10, 4, true)]
    public async Task DifferentStatePostsAtTopLeftThenClearsSavedBits(byte style, byte state, bool on)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 701, state: state, style: style);
        var platform = new RecordingPlatform();
        platform.Posting = message =>
        {
            if (message == 0x202)
            {
                // 配信要求後に状態が変わっても、保存済みbitだけで解除を決めます。
                Native.SendMessageW(toolbar, 0x411, 701, on ? 7 : 4);
            }
        };
        int commands = 0;
        fixture.Command = (_, _) => commands++;
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, parameter, pointer) =>
        {
            if (message == 0x41d)
            {
                Marshal.WriteInt32(pointer, 10);
                Marshal.WriteInt32(pointer, 4, 20);
                Marshal.WriteInt32(pointer, 8, 30);
                Marshal.WriteInt32(pointer, 12, 40);
                return 0;
            }

            if (message is 0x402 or 0x403)
            {
                Assert.Equal(701, parameter);
                Assert.Equal(0, pointer);
                platform.Events.Add(message == 0x403 ? "pressed0" : "checked0");
                return 0;
            }

            return null;
        });
        await using IWindowsAutomationSession session = Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(64), Value.Numeric(on ? 1 : 0)], default));
        List<string> expected = ["priority256", "post513", "post514", "priority32"];
        if (!on && (state & 2) != 0)
        {
            expected.Add("pressed0");
        }

        if (!on && (state & 1) != 0)
        {
            expected.Add("checked0");
        }

        Assert.Equal(expected, platform.Events);
        Assert.Equal(new[] { (toolbar, 0x201u, (nint)0, (nint)0x14000a),
            (toolbar, 0x202u, (nint)0, (nint)0x14000a) }, platform.Posts);
        Assert.Equal(0, commands);
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtendedCommandIdDoesNotRequireParentWordPayload(bool on)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 65537, state: on ? (byte)4 : (byte)5);
        var platform = new RecordingPlatform();
        using var hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, command, _) =>
        {
            if (message == 0x402)
            {
                Assert.Equal(65537, command);
                platform.Events.Add("checked0");
            }

            return null;
        });
        await using IWindowsAutomationSession session = Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(64), Value.Numeric(on ? 1 : 0)], default));
        Assert.Equal(2, platform.Posts.Count);
        Assert.Equal(!on, platform.Events.Contains("checked0"));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostFailureOrCancellationStillRequestsUpAndReleasesBuffer(bool throwing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "target", 701, state: 7, style: 2);
        using var cancel = new CancellationTokenSource();
        var platform = new RecordingPlatform();
        platform.Posting = message =>
        {
            if (message == 0x201)
            {
                if (throwing)
                {
                    throw new InvalidOperationException("post callback");
                }

                cancel.Cancel();
            }
        };
        await using IWindowsAutomationSession session = Session(platform);
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String("target"), Value.Numeric(64), Value.Numeric(0)];
        if (throwing)
        {
            Assert.Equal(Value.Numeric(1),
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancel.Token));
        }

        Assert.Equal(["priority256", "post513", "post514", "priority32"], platform.Events);
        Assert.Equal(7, Native.SendMessageW(toolbar, 0x412, 701, 0));
        Assert.True(platform.Buffers.Released);
        Assert.True(platform.Buffers.Closed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task OwnedPumpedToolbarReceivesActualPostedMessages()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<uint> messages = [];
        ToolbarClickTextTests.Interceptor? hook = null;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint toolbar = ToolbarControlTests.Create(host, 10);
            ToolbarControlTests.Add(toolbar, "target", 701);
            hook = new ToolbarClickTextTests.Interceptor(toolbar, (message, parameter, _) =>
            {
                if (message is 0x201 or 0x202)
                {
                    Assert.Equal(0, parameter);
                    messages.Add(message);
                    if (message == 0x202)
                    {
                        received.TrySetResult();
                    }
                }

                return null;
            });
        });
        try
        {
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: new OwnedListSelectionPlatform()).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("target"), Value.Numeric(64)], default));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new uint[] { 0x201, 0x202 }, messages);
        }
        finally
        {
            hook?.Dispose();
        }
    }

    internal static void AddNativeCandidates(ButtonControlTests.Fixture fixture)
    {
        // native限定の試験として、既存20候補境界でUIA fallbackを抑止します。
        for (int index = 0; index < 20; index++)
        {
            fixture.Child("LISTBOX", "padding" + index, 0, 200 + index);
        }
    }
}
