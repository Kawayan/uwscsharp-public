using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SelectiveInputFrontendTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string frontend in new[] { "UWSCSharp.Cli", "UWSCSharp.Wpf" })
        {
            foreach (bool global in new[] { false, true })
            {
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    foreach (string ending in new[] { "release", "normal", "error", "cancel" })
                    {
                        yield return [frontend, global, mode, ending];
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PublicScriptBlocksSelectedInputAndRestoresDelivery(
        string frontend, bool global, int mode, string ending)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var received = new ConcurrentQueue<(uint Message, nint Key)>();
        await using PumpedControlFixture receiver = await PumpedControlFixture.CreateAsync(owner =>
            owner.InputMessage = (message, key, _) => received.Enqueue((message, key)),
            dpiAwarenessContext: -4);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-selective-frontend-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var samples = new List<object>();
        object? stopPlacement = null;
        try
        {
            int stopX;
            int stopY;
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(Native.GetWindowRect(receiver.Root, out Native.Rect bounds));
                stopX = checked(bounds.Right + 20);
                stopY = checked(bounds.Bottom + 20);
            }
            string script = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(script, $$"""
                WaitGate("start")
                {{(ending == "cancel" ? $"STOPFORM(TRUE,{stopX},{stopY})\nWrite(\"stop-window\",IDTOHND(GETID(GET_STOPFORM_WIN)))" : "")}}
                Write("before",1)
                WaitGate("lock")
                DIM target={{(global ? "0" : $"HNDTOID({(long)receiver.Root})")}}
                Write("target-id",target)
                LOCKHARDEX(target,{{mode}})
                LOCKHARDEX(target,{{mode}})
                Write("held",1)
                WaitGate("release")
                {{(ending == "normal" ? "EXITEXIT" : ending == "error" ? "DIM failure=1/0" : "")}}
                LOCKHARDEX()
                Write("released",1)
                WaitGate("exit")
                EXITEXIT
                PROCEDURE Write(name,text)
                    DIM file=FOPEN("{{directory}}\"+name,F_WRITE8)
                    FPUT(file,text,0)
                    FCLOSE(file)
                FEND
                PROCEDURE WaitGate(name)
                    WHILE !FOPEN("{{directory}}\"+name,F_EXISTS)
                        SLEEP(0.01)
                    WEND
                FEND
                """, new UTF8Encoding(true));
            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" \"{script}\"", TimeSpan.FromSeconds(25));
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            nint stopWindow = 0;
            try
            {
                await MarkAsync("start");
                await WaitFileAsync("before");
                if (ending == "cancel")
                {
                    stopWindow = checked((nint)long.Parse(
                        await File.ReadAllTextAsync(Path.Combine(directory, "stop-window")),
                        CultureInfo.InvariantCulture));
                    Native.GetWindowThreadProcessId(stopWindow, out uint owner);
                    Assert.Equal((uint)process.Id, owner);
                    using var dpi = new PhysicalDpiScope();
                    Assert.True(Native.GetWindowRect(stopWindow, out Native.Rect stopBounds));
                    Assert.True(Native.GetWindowRect(receiver.Root, out Native.Rect receiverBounds));
                    stopPlacement = new
                    {
                        requested_x = stopX,
                        requested_y = stopY,
                        stop = new[] { stopBounds.Left, stopBounds.Top, stopBounds.Right, stopBounds.Bottom },
                        receiver = new[] { receiverBounds.Left, receiverBounds.Top, receiverBounds.Right, receiverBounds.Bottom }
                    };
                }
                await UiInputDriver.ActivateAsync(receiver.Root);
                receiver.Invoke(() => SetFocus(receiver.Root));
                using (var dpi = new PhysicalDpiScope())
                {
                    var point = new Native.Point(80, 80);
                    Assert.True(Native.ClientToScreen(receiver.Root, ref point));
                    Assert.True(Native.SetPhysicalCursorPos(point.X, point.Y));
                    Assert.Equal(receiver.Root, Native.WindowFromPoint(point));
                }
                await SampleAsync("before", true, true);
                await MarkAsync("lock");
                await WaitFileAsync("held");
                int targetId = int.Parse(await File.ReadAllTextAsync(
                    Path.Combine(directory, "target-id")), CultureInfo.InvariantCulture);
                Assert.True(global ? targetId == 0 : targetId > 0);
                await SampleAsync("held", mode == 2, mode == 1);
                Assert.False(process.HasExited);
                if (ending == "cancel")
                {
                    Assert.True(Native.PostMessageW(stopWindow, 0x10, 0, 0));
                }
                else
                {
                    await MarkAsync("release");
                }
                if (ending == "release")
                {
                    await WaitFileAsync("released");
                    Assert.False(process.HasExited);
                    await SampleAsync("released-alive", true, true);
                    await MarkAsync("exit");
                }
                await process.WaitForExitAsync(deadline.Token);
                Assert.Equal(ending == "cancel" ? 130 : ending == "error" ? 1 : 0, process.ExitCode);
                await UiInputDriver.ActivateAsync(receiver.Root);
                receiver.Invoke(() => SetFocus(receiver.Root));
                await SampleAsync("after-exit", true, true);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    frontend,
                    global,
                    mode,
                    ending,
                    exit_code = process.ExitCode,
                    process_id = process.Id,
                    receiver_handle = (long)receiver.Root,
                    target_id = targetId,
                    stopPlacement,
                    samples,
                    limits = "入力はSendInputの注入。終了後の回復は明示回収とOS終了時回収を区別しない。"
                }));
            }
            catch
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    frontend,
                    global,
                    mode,
                    ending,
                    stopPlacement,
                    samples
                }));
                if (process.HasExited)
                {
                    output.WriteLine(Encoding.UTF8.GetString(
                        await watchdog.Output.WaitAsync(TimeSpan.FromSeconds(3))));
                }
                throw;
            }

            Task MarkAsync(string name) => File.WriteAllTextAsync(Path.Combine(directory, name), "go");

            async Task WaitFileAsync(string name)
            {
                string path = Path.Combine(directory, name);
                while (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    Assert.False(process.HasExited, "通知前に対象プロセスが終了しました。");
                    await Task.Delay(10, deadline.Token);
                }
            }

            async Task SampleAsync(string phase, bool keyboard, bool mouse)
            {
                Assert.Equal(receiver.Root, Native.GetForegroundWindow());
                using (var dpi = new PhysicalDpiScope())
                {
                    Assert.True(Native.GetPhysicalCursorPos(out Native.Point position));
                    Assert.Equal(receiver.Root, Native.WindowFromPoint(position));
                }
                receiver.Invoke(() => received.Clear());
                Native.Input[] inputs = [Mouse(0x20), Mouse(0x40), Key(0x87, 0, 0),
                    Key(0x87, 2, 0), Key(0x86, 0, 1), Key(0x86, 2, 1)];
                Assert.Equal((uint)inputs.Length, Native.SendInput((uint)inputs.Length,
                    inputs, Marshal.SizeOf<Native.Input>()));
                while (!received.Any(item => item.Message == 0x101 && item.Key == 0x86))
                {
                    await Task.Delay(10, deadline.Token);
                }
                receiver.Invoke(() => { });
                (uint Message, nint Key)[] events = [.. received];
                int keyDown = events.Count(item => item.Message == 0x100 && item.Key == 0x87);
                int keyUp = events.Count(item => item.Message == 0x101 && item.Key == 0x87);
                int mouseDown = events.Count(item => item.Message is 0x207 or 0x209);
                int mouseUp = events.Count(item => item.Message == 0x208);
                samples.Add(new
                {
                    phase,
                    keyDown,
                    keyUp,
                    mouseDown,
                    mouseUp
                });
                Assert.Equal(keyboard ? 1 : 0, keyDown);
                Assert.Equal(keyboard ? 1 : 0, keyUp);
                Assert.Equal(mouse ? 1 : 0, mouseDown);
                Assert.Equal(mouse ? 1 : 0, mouseUp);
                Assert.Equal(receiver.Root, Native.GetForegroundWindow());
            }
        }
        finally
        {
            // 検証が途中で失敗しても、ゲスト内の注入キーとボタンを解放します。
            Native.Input[] releases = [Key(0x87, 2, 1), Key(0x86, 2, 1), Mouse(0x40, 1)];
            Native.SendInput((uint)releases.Length, releases, Marshal.SizeOf<Native.Input>());
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert.StartsWith(root, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Native.Input Key(ushort key, uint flags, nuint extra) => new()
    {
        Type = 1,
        Data = new Native.InputUnion
        {
            Keyboard = new Native.KeyboardInput { Vk = key, Flags = flags, Extra = extra }
        }
    };

    private static Native.Input Mouse(uint flags, nuint extra = 0) => new()
    {
        Type = 0,
        Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags, Extra = extra } }
    };

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);
}
