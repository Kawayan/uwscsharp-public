using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SelectiveInputEditorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task SameEditorReleasesNativeHooksAndRunsAgain(bool global, int mode)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var received = new ConcurrentQueue<(uint Message, nint Key)>();
        await using PumpedControlFixture receiver = await PumpedControlFixture.CreateAsync(owner =>
            owner.InputMessage = (message, key, _) => received.Enqueue((message, key)),
            dpiAwarenessContext: -4);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-selective-editor-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var samples = new List<object>();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "request.json"),
                JsonSerializer.Serialize(new
                {
                    global,
                    mode,
                    receiver = (long)receiver.Root
                }));
            string executable = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" --verify-selective-input-editor \"{directory}\"",
                TimeSpan.FromSeconds(30));
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await WaitFileAsync("ready");
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
                string[] endings = ["release", "retry", "normal", "retry", "error", "retry", "cancel", "retry"];
                for (int run = 0; run < endings.Length; run++)
                {
                    string prefix = run + "-";
                    await MarkAsync(prefix + "begin");
                    await WaitFileAsync(prefix + "held");
                    await SampleAsync(prefix + "held", mode == 2, mode == 1);
                    if (endings[run] == "cancel")
                    {
                        await MarkAsync(prefix + "cancel");
                    }
                    else
                    {
                        await MarkAsync(prefix + "finish");
                    }
                    if (endings[run] == "release")
                    {
                        await WaitFileAsync(prefix + "released");
                        await SampleAsync(prefix + "released", true, true);
                        await MarkAsync(prefix + "exit");
                    }
                    await WaitFileAsync(prefix + "ended");
                    Assert.False(process.HasExited);
                    await SampleAsync(prefix + "ended", true, true);
                    await MarkAsync(prefix + "observed");
                }
                await process.WaitForExitAsync(deadline.Token);
                string receipt = await File.ReadAllTextAsync(Path.Combine(directory, "receipt.json"));
                output.WriteLine(receipt);
                using var result = JsonDocument.Parse(receipt);
                JsonElement root = result.RootElement;
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("PASS", root.GetProperty("result").GetString());
                Assert.Equal(process.Id, root.GetProperty("process_id").GetInt32());
                long window = root.GetProperty("handle").GetInt64();
                Assert.NotEqual(0, window);
                JsonElement[] checks = [.. root.GetProperty("checks").EnumerateArray()];
                Assert.Equal(8, checks.Length);
                for (int run = 0; run < checks.Length; run++)
                {
                    JsonElement check = checks[run];
                    Assert.Equal(run, check.GetProperty("run").GetInt32());
                    Assert.Equal(endings[run], check.GetProperty("ending").GetString());
                    Assert.Equal(mode, check.GetProperty("mode").GetInt32());
                    Assert.Equal(global, check.GetProperty("global").GetBoolean());
                    Assert.Equal(window, check.GetProperty("handle").GetInt64());
                    Assert.Equal(0, check.GetProperty("pending").GetInt32());
                    Assert.True(check.GetProperty("starts_restored").GetBoolean());
                    Assert.Equal(endings[run] == "error" ? 1 : 0,
                        check.GetProperty("errors").GetInt32());
                }
            }
            finally
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    global,
                    mode,
                    samples,
                    limits = "実フックとSendInputの注入入力。表示した同一WPF編集画面のClickイベントを送る。物理クリック・実OS解除障害・競合・原版比較は対象外。"
                }));
                string path = Path.Combine(directory, "receipt.json");
                if (File.Exists(path))
                {
                    output.WriteLine(await File.ReadAllTextAsync(path));
                }
            }

            Task MarkAsync(string name) => File.WriteAllTextAsync(Path.Combine(directory, name), "go");

            async Task WaitFileAsync(string name)
            {
                string path = Path.Combine(directory, name);
                while (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    Assert.False(process.HasExited, "通知前にWPFが終了しました: " + name);
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
            // ゲスト内の注入入力だけを解放し、今回の一時フォルダーを削除します。
            Native.Input[] releases = [Key(0x87, 2, 1), Key(0x86, 2, 1), Mouse(0x40, 1)];
            Native.SendInput((uint)releases.Length, releases, Marshal.SizeOf<Native.Input>());
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert.StartsWith(temp, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
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
