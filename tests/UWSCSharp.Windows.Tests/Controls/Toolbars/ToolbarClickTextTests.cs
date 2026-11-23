using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ToolbarClickTextTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint process, nint address,
        nuint size, uint freeType);

    internal sealed class BufferPlatform : INativeControlBufferPlatform
    {
        private readonly NativeControlBufferPlatform native = new();
        internal bool Released
        {
            get; private set;
        }
        internal bool Closed
        {
            get; private set;
        }
        internal nint Address
        {
            get; private set;
        }

        public nint Open(nint root) => native.Open(root);

        public nint Allocate(nint process)
        {
            Address = native.Allocate(process);
            return Address;
        }

        public void Free(nint process, nint address)
        {
            Assert.Equal(Address, address);
            Released = VirtualFreeEx(process, address, 0, 0x8000);
            Assert.True(Released);
        }

        public void Close(nint process)
        {
            native.Close(process);
            Closed = true;
        }
    }

    internal sealed class RestorePlatform : OwnedListSelectionPlatform
    {
        internal Action<nint>? Preparing
        {
            get; init;
        }

        internal Action? Restoring
        {
            get; init;
        }
        internal BufferPlatform Buffers { get; } = new();

        public override NativeControlBufferOwner CreateControlBufferOwner(nint root) =>
            new(root, Buffers);

        public override nint ForegroundWindow() => 77;

        public override void Activate(nint root, CancellationToken token)
        {
            if (root != 77)
            {
                Assert.NotNull(Preparing);
                Preparing(root);
                return;
            }

            Assert.Equal((nint)77, root);
            Assert.False(token.IsCancellationRequested);
            Restoring?.Invoke();
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("cancel")]
    public async Task CandidateBuffersShareAddressAndAreFreedBeforeForegroundRestore(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = ToolbarControlTests.Create(fixture, 10);
        nint second = ToolbarControlTests.Create(fixture, 40);
        ToolbarControlTests.Add(first, "first", 701);
        ToolbarControlTests.Add(second, "second", 702);
        using var cancellation = new CancellationTokenSource();
        List<nint> pointers = [];
        nint? Observe(uint message, nint parameter, nint pointer)
        {
            if (message == 0x44b && pointer != 0)
            {
                pointers.Add(pointer);
                if (outcome == "cancel" && message == 0x44b)
                {
                    cancellation.Cancel();
                }
            }

            return null;
        }

        using var firstHook = new Interceptor(first, Observe);
        using var secondHook = new Interceptor(second, Observe);
        Native.ShowWindow(fixture.Root, 0);
        int restores = 0;
        RestorePlatform? platform = null;
        platform = new RestorePlatform
        {
            Restoring = () =>
            {
                restores++;
                Assert.NotEmpty(pointers);
                Assert.True(platform!.Buffers.Released);
                Assert.True(platform.Buffers.Closed);
                Assert.Equal(platform.Buffers.Address, pointers[0]);
            },
        };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String(outcome == "missing" ? "missing" : "second"),
            Value.Numeric(64 | (outcome == "missing" ? 536870912 : 0))];
        if (outcome == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(outcome == "success" ? 1 : 0),
                await session.InvokeAsync("CLKITEM", args, default));
            Assert.True(pointers.Count >= 2);
        }

        Assert.All(pointers, pointer => Assert.Equal(pointers[0], pointer));
        Assert.Equal(1, restores);
    }

    [Fact]
    public void NegativeTextResultStillReadsBytesWrittenByControl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "stored", 701);
        using var hook = new Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message == 0x44b && pointer != 0)
            {
                char[] text = "failure text\0".ToCharArray();
                Marshal.Copy(text, 0, pointer, text.Length);
                return -1;
            }

            return null;
        });
        Assert.Equal((1, true, true), NativeToolbarControl.Click(toolbar,
            "failure text", 1, false, default,
            platform: new ToolbarOperationTests.RecordingPlatform()));
    }

    [Fact]
    public void FirstMatchDoesNotReadLaterFailingText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "first", 701);
        ToolbarControlTests.Add(toolbar, "later", 702);
        int laterReads = 0;
        using var hook = new Interceptor(toolbar, (message, command, _) =>
        {
            if (message == 0x44b && command == 702)
            {
                laterReads++;
                return 65535;
            }

            return null;
        });
        Assert.Equal((1, true, true), NativeToolbarControl.Click(toolbar,
            "first", 1, false, default,
            platform: new ToolbarOperationTests.RecordingPlatform()));
        Assert.Equal(0, laterReads);
    }

    private delegate nint Procedure(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern nint SetLong(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint window,
        uint message, nint wParam, nint lParam);

    private static nint SetProcedure(nint window, nint procedure) =>
        IntPtr.Size == 8 ? SetLongPtr(window, -4, procedure) : SetLong(window, -4, procedure);

    internal sealed class Interceptor : IDisposable
    {
        private readonly nint target;
        private readonly nint previous;
        private readonly Procedure procedure;

        internal Interceptor(nint target, Func<uint, nint, nint, nint?> response,
            bool removeTextPointer = false, Action<uint, nint, nint>? after = null)
        {
            this.target = target;
            procedure = (window, message, wParam, lParam) =>
            {
                nint result = response(message, wParam, lParam)
                    ?? CallWindowProcW(previous, window, message, wParam, lParam);
                if (removeTextPointer && message == 0x417 && result != 0)
                {
                    Marshal.WriteIntPtr(lParam, IntPtr.Size == 8 ? 24 : 16, -1);
                }

                after?.Invoke(message, wParam, lParam);

                return result;
            };
            previous = SetProcedure(target, Marshal.GetFunctionPointerForDelegate(procedure));
            Assert.NotEqual(0, previous);
        }

        public void Dispose()
        {
            SetProcedure(target, previous);
            GC.KeepAlive(procedure);
        }
    }

    [Fact]
    public void ClickUsesCommandTextEvenWhenButtonHasNoTextPointer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "stored name", 701);
        int stringRequests = 0;
        int commandRequests = 0;
        int commands = 0;
        fixture.Command = (_, _) => commands++;
        using var intercept = new Interceptor(toolbar, (message, command, pointer) =>
        {
            if (message == 0x45b)
            {
                stringRequests++;
                return -1;
            }

            if (message == 0x44b)
            {
                commandRequests++;
                if (command != 701)
                {
                    return -1;
                }

                if (pointer != 0)
                {
                    char[] text = "command name\0".ToCharArray();
                    Marshal.Copy(text, 0, pointer, text.Length);
                }

                return 12;
            }

            return null;
        }, removeTextPointer: true);
        Assert.Equal((1, true, true), NativeToolbarControl.Click(toolbar,
            "command name", 1, true, default,
            platform: new ToolbarOperationTests.RecordingPlatform()));
        Assert.Equal(0, stringRequests);
        Assert.Equal(2, commandRequests);
        Assert.Equal(0, commands);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(513)]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(6000)]
    public async Task ClickMatchesOnlyFirst512CharactersButItemReaderKeepsFullName(int length)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        string prefix = new('x', 512);
        string full = prefix + new string('y', length - prefix.Length);
        ToolbarControlTests.Add(toolbar, full, 701);
        Assert.Equal((1, true, true), NativeToolbarControl.Click(toolbar,
            prefix, 1, false, default,
            platform: new ToolbarOperationTests.RecordingPlatform()));
        Assert.Equal((0, false, false), NativeToolbarControl.Click(toolbar,
            full, 1, false, default,
            platform: new ToolbarOperationTests.RecordingPlatform()));
        Assert.Contains(full, await NativeToolbarControl.ReadAsync(toolbar, false, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextFailureOrCapacityOverflowDoesNotSendParentCommand(bool oversized)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint toolbar = ToolbarControlTests.Create(fixture, 10);
        ToolbarControlTests.Add(toolbar, "name", 701);
        int writes = 0;
        int commands = 0;
        fixture.Command = (_, _) => commands++;
        using var intercept = new Interceptor(toolbar, (message, _, pointer) =>
        {
            if (message != 0x44b)
            {
                return null;
            }

            writes += pointer != 0 ? 1 : 0;
            return oversized ? 65535 : -1;
        });
        if (oversized)
        {
            Assert.Throws<NotSupportedException>(() =>
                NativeToolbarControl.Click(toolbar, "name", 1, true, default,
                    platform: new ToolbarOperationTests.RecordingPlatform()));
            Assert.Equal(0, writes);
        }
        else
        {
            Assert.Equal((0, false, false), NativeToolbarControl.Click(toolbar,
                "name", 1, true, default,
                platform: new ToolbarOperationTests.RecordingPlatform()));
            Assert.Equal(1, writes);
        }

        Assert.Equal(0, commands);
    }
}
