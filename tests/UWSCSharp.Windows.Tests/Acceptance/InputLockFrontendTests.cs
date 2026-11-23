using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputLockFrontendTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("UWSCSharp.Cli", "release")]
    [InlineData("UWSCSharp.Cli", "normal")]
    [InlineData("UWSCSharp.Cli", "error")]
    [InlineData("UWSCSharp.Cli", "cancel")]
    [InlineData("UWSCSharp.Wpf", "release")]
    [InlineData("UWSCSharp.Wpf", "normal")]
    [InlineData("UWSCSharp.Wpf", "error")]
    [InlineData("UWSCSharp.Wpf", "cancel")]
    public async Task FrontendOwnsInputAndReleasesIt(string frontend, string ending)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-input-lock-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await using PumpedControlFixture? focus = ending == "cancel"
                ? await PumpedControlFixture.CreateAsync(_ => { }) : null;
            string script = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(script, $$"""
                WaitGate("start")
                {{(ending == "cancel" ? "STOPFORM(TRUE)\nWrite(\"stop-window\",IDTOHND(GETID(GET_STOPFORM_WIN)))" : "")}}
                LOCKHARD(TRUE)
                LOCKHARD(TRUE)
                KBD(135,1)
                SLEEP(0.05)
                Write("down",GETKEYSTATE(135))
                {{(ending == "cancel" ? "" : "KBD(135,2)")}}
                SLEEP(0.05)
                Write("held",GETKEYSTATE(135))
                WaitGate("release")
                {{(ending == "normal" ? "EXITEXIT" : ending == "error" ? "DIM failure=1/0" : "")}}
                {{(frontend == "UWSCSharp.Cli" ? "LOCKHARD()" : "LOCKHARD(FALSE)")}}
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
            output.WriteLine(await File.ReadAllTextAsync(script));
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" \"{script}\"", TimeSpan.FromSeconds(15));
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            if (focus is not null)
            {
                await UiInputDriver.ActivateAsync(focus.Root);
                Assert.True(AllowSetForegroundWindow((uint)process.Id));
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "start"), "go");
            try
            {
                await WaitFileAsync(Path.Combine(directory, "held"), process);
            }
            catch
            {
                if (process.HasExited)
                {
                    output.WriteLine(Encoding.UTF8.GetString(
                        await watchdog.Output.WaitAsync(TimeSpan.FromSeconds(5))));
                }
                throw;
            }
            Assert.Equal("1", (await File.ReadAllTextAsync(Path.Combine(directory, "down"))).Trim());
            Assert.Equal(ending == "cancel" ? "1" : "0",
                (await File.ReadAllTextAsync(Path.Combine(directory, "held"))).Trim());
            JsonElement held = await ProbeAsync("held-probe");
            Assert.False(process.HasExited);
            Assert.False(held.GetProperty("blocked").GetBoolean());
            if (ending == "cancel")
            {
                nint window = checked((nint)long.Parse(
                    await File.ReadAllTextAsync(Path.Combine(directory, "stop-window")),
                    CultureInfo.InvariantCulture));
                Assert.True(Native.IsWindow(window));
                Native.GetWindowThreadProcessId(window, out uint owner);
                Assert.Equal((uint)process.Id, owner);
                Assert.True(Native.PostMessageW(window, 0x10, 0, 0));
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "release"), "go");
            }
            if (ending == "release")
            {
                await WaitFileAsync(Path.Combine(directory, "released"), process);
                Assert.False(process.HasExited);
            }
            else
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (ending == "cancel")
            {
                Assert.Equal(130, process.ExitCode);
                Assert.Equal(0, new NativeKeyStateBackend().ReadAsync(135) & 0x8000);
                Assert.False(File.Exists(Path.Combine(directory, "released")));
            }

            JsonElement released = await ProbeAsync("released-probe");
            Assert.Equal(JsonValueKind.Null, released.GetProperty("failure").ValueKind);
            Assert.True(released.GetProperty("reacquired").GetBoolean());
            if (ending == "release")
            {
                Assert.False(process.HasExited);
                await File.WriteAllTextAsync(Path.Combine(directory, "exit"), "go");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (ending == "error")
            {
                Assert.NotEqual(0, process.ExitCode);
            }
            else if (ending != "cancel")
            {
                Assert.Equal(0, process.ExitCode);
            }
            output.WriteLine(JsonSerializer.Serialize(new
            {
                frontend,
                ending,
                held,
                released,
                exit_code = process.ExitCode
            }));

            async Task<JsonElement> ProbeAsync(string phase)
            {
                string marker = Path.Combine(directory, phase);
                string fixture = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
                await using SandboxProcessWatchdog probe = await SandboxProcessWatchdog.StartAsync(
                    fixture, $"\"{fixture}\" --input-ownership-probe \"{marker}\"", TimeSpan.FromSeconds(3));
                using var target = Process.GetProcessById(checked((int)probe.ProcessId));
                _ = target.SafeHandle;
                await File.WriteAllTextAsync(marker + ".go", "go");
                await WaitFileAsync(marker, target);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
                return document.RootElement.Clone();
            }
        }
        finally
        {
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert.StartsWith(root, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitFileAsync(string path, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            Assert.False(process.HasExited, "通知前に対象が終了しました。");
            await Task.Delay(10, timeout.Token);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
