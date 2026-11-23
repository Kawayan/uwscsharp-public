using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class RemoteTabControlTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Native.Point Point;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherThreadReadsAndSelectsWithParentNotification(bool reselect)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint threadId = 0;
        Exception? failure = null;
        List<int> notices = [];
        var owner = new Thread(() =>
        {
            try
            {
                using var fixture = new ButtonControlTests.Fixture();
                SliderControlTests.InitializeControls(8);
                nint tab = fixture.Child("SysTabControl32", "", 0, 10);
                for (int index = 0; index < 2; index++)
                {
                    nint text = Marshal.StringToHGlobalUni(index == 0 ? "first" : "日本語");
                    nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeTabControl.Item>());
                    try
                    {
                        var item = new NativeTabControl.Item { Mask = 1, Text = text };
                        Marshal.StructureToPtr(item, pointer, false);
                        Assert.Equal(index, Native.SendMessageW(tab, 0x133e, index, pointer));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pointer);
                        Marshal.FreeHGlobal(text);
                    }
                }

                fixture.Notify = (_, pointer) =>
                {
                    NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
                    notices.Add(notice.Code);
                    if (reselect && notice.Code == -551)
                    {
                        Native.SendMessageW(tab, 0x130c, 0, 0);
                    }

                    return 1;
                };
                threadId = Native.GetCurrentThreadId();
                ready.SetResult(fixture.Root);
                while (GetMessageW(out Message message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                failure = fixture.CallbackFailure;
            }
            catch (Exception exception)
            {
                failure = exception;
                ready.TrySetException(exception);
            }
        })
        {
            IsBackground = true
        };
        owner.Start();
        try
        {
            nint root = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform())
                .CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)root)], default);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM", [id, Value.Numeric(8)], default));
            Assert.Equal(["first", "日本語"], session.AllItems);
            Assert.Equal(Value.Numeric(reselect ? 0 : 1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("日本語"), Value.Numeric(8)], default));
        }
        finally
        {
            if (threadId != 0)
            {
                PostThreadMessageW(threadId, 0x12, 0, 0);
            }

            Assert.True(owner.Join(5000));
        }

        Assert.Null(failure);
        Assert.Equal([-552, -552, -551], notices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerStopsWaitingButWorkerRetainsPointerUntilMessageReturns(bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int observed = 0;
        Task<int> pending = NativeControlWorker.RunAsync(_ =>
        {
            nint pointer = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(pointer, 12345);
                entered.Set();
                Assert.True(release.Wait(5000));
                observed = Marshal.ReadInt32(pointer);
                return observed;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
                finished.Set();
            }
        }, cancellation.Token, TimeSpan.FromMilliseconds(100));
        try
        {
            Assert.True(entered.Wait(5000));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => pending);
            }

            Assert.False(finished.IsSet);
        }
        finally
        {
            release.Set();
            Assert.True(finished.Wait(5000));
        }

        Assert.Equal(12345, observed);
    }

    [Fact]
    public async Task BlockedWorkersAreBoundedAndSlotsRecover()
    {
        using var entered = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        List<Task<int>> operations = [];
        for (int index = 0; index < 4; index++)
        {
            operations.Add(NativeControlWorker.RunAsync(_ =>
            {
                entered.Signal();
                Assert.True(release.Wait(5000));
                return 1;
            }, default, TimeSpan.FromSeconds(5)));
        }

        try
        {
            Assert.True(entered.Wait(5000));
            Assert.Equal(4, NativeControlWorker.Active);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                NativeControlWorker.RunAsync(_ => 0, default, TimeSpan.FromMilliseconds(20)));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(operations);
        }

        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }

    [Fact]
    public async Task ChildProcessRemoteTextBufferAndSelectionPreflight()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string executable = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        Assert.True(child.Start());
        try
        {
            string? line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(line);
            long[] handles = [.. line.Split(',').Select(long.Parse)];
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform())
                .CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric(handles[0])], default);
            Assert.Equal(Value.String("M3 child process fixture"), await session.InvokeAsync("GETSTR",
                [id, Value.Numeric(0), Value.Numeric(1)], default));
            Assert.Equal(Value.Numeric(5), await session.InvokeAsync("GETITEM", [id, Value.Numeric(8)], default));
            Assert.Equal(["日本語", "", "same", "same", new string('長', 255)], session.AllItems);
            IReadOnlyList<string> snapshot = session.AllItems;
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
                await session.InvokeAsync("CLKITEM",
                    [id, Value.String("same"), Value.Numeric(8 | 536870912)], default));
            Assert.Same(snapshot, session.AllItems);
            Assert.Equal(0, Native.SendMessageW((nint)handles[1], 0x130b, 0, 0));
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("same"), Value.Numeric(8 | 512), Value.Numeric(1), Value.Numeric(2)], default));
            Assert.Equal(3, Native.SendMessageW((nint)handles[1], 0x130b, 0, 0));
            Assert.Same(snapshot, session.AllItems);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM", [id, Value.Numeric(16)], default));
            Assert.Equal(["親", "親\\&子..."], session.AllItems);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM", [id, Value.Numeric(32)], default));
            Assert.Equal(["one", "two"], session.AllItems);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(32), Value.Numeric(1), Value.Numeric(2)], default));
            Assert.Equal(["外部0", "外部1"], session.AllItems);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(32), Value.Numeric(1), Value.Numeric(-1)], default));
            Assert.Equal(["Key", "Value"], session.AllItems);
            Assert.Equal(" 親 \\ &子... ", (await session.InvokeAsync("GETSLCTLST",
                [id, Value.Numeric(1)], default)).Display());
            Assert.Equal("one\ttwo", (await session.InvokeAsync("GETSLCTLST",
                [id, Value.Numeric(2)], default)).Display());
            Assert.Equal("外部0\t外部1", (await session.InvokeAsync("GETSLCTLST",
                [id, Value.Numeric(2), Value.Numeric(2)], default)).Display());
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("親"), Value.Numeric(16)], default));
            Assert.Equal(" 親 ", (await session.InvokeAsync("GETSLCTLST",
                [id, Value.Numeric(1)], default)).Display());
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("two"), Value.Numeric(32)], default));
            Assert.Equal("two", (await session.InvokeAsync("GETSLCTLST",
                [id, Value.Numeric(2)], default)).Display());
            Assert.Equal("外部1", (await session.InvokeAsync("GETSLCTLST",
                [id, Value.Numeric(2), Value.Numeric(2)], default)).Display());
            foreach ((string name, int kind) in new[] { ("tree-check", 16), ("remote-check1", 32) })
            {
                Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CHKBTN",
                    [id, Value.String(name)], default));
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                    [id, Value.String(name), Value.Numeric(kind)], default));
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKBTN",
                    [id, Value.String(name)], default));
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                    [id, Value.String(name), Value.Numeric(kind), Value.Numeric(0)], default));
                Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CHKBTN",
                    [id, Value.String(name)], default));
            }

            Assert.Equal(Value.Numeric(3), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(524288)], default));
            Assert.Equal("remote", session.AllItems[0]);
            Assert.Equal("", session.AllItems[1]);
            Assert.Equal(new string('長', 65534), session.AllItems[2]);
            Assert.Equal("remote", (await session.InvokeAsync("GETSTR",
                [id, Value.Numeric(0), Value.Numeric(2)], default)).Display());
            Assert.Equal(session.AllItems[2], (await session.InvokeAsync("GETSTR",
                [id, Value.Numeric(3), Value.Numeric(2)], default)).Display());
            Assert.Equal(Value.Numeric(3), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(64)], default));
            Assert.Equal(["remote toolbar", new string('長', 4097), "remote dropdown"], session.AllItems);
            Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
                [id, Value.String("remote toolbar")], default));
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("remote toolbar"), Value.Numeric(64)], default));
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("remote dropdown"), Value.Numeric(64)], default));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
