using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class NativeDllFrontendLifetimeTests
{
    [Theory]
    [InlineData("ctrl-c", false)]
    [InlineData("ctrl-c", true)]
    [InlineData("break", false)]
    [InlineData("break", true)]
    public async Task ConsoleCancellationRetainsNativeCalls(string mode,
        bool externalCall)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var probe = new Probe(externalCall, exit: false);
        string signal = "Local\\UWSCNativeSignal-" + Guid.NewGuid().ToString("N");
        using var request = new EventWaitHandle(false,
            EventResetMode.ManualReset, signal + "-request");
        using var sent = new EventWaitHandle(false,
            EventResetMode.ManualReset, signal + "-sent");
        string receipt = Path.Combine(probe.Directory, "console.json");
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
        using Process helper = StartCancellationProcess(fixture, probe.Directory,
            ["--cli-native-dll-console-probe", cli, mode, probe.Main,
                signal + "-request", signal + "-sent", receipt]);
        Task<string> stderr = helper.StandardError.ReadToEndAsync();
        Task<string> stdout = helper.StandardOutput.ReadToEndAsync();
        Task exited = helper.WaitForExitAsync();
        try
        {
            var waiting = Stopwatch.StartNew();
            while (!File.Exists(receipt + ".ready"))
            {
                Assert.False(helper.HasExited);
                Assert.True(waiting.Elapsed < Timeout, "CLIの起動待ちが期限を超えました。");
                await Task.Delay(10);
            }
            int pid = int.Parse(await File.ReadAllTextAsync(receipt + ".ready"),
                CultureInfo.InvariantCulture);
            using var child = Process.GetProcessById(pid);
            Assert.Equal(cli, child.MainModule!.FileName, ignoreCase: true);
            await WaitForBlockedCallsAsync(child, probe);
            request.Set();
            Assert.True(await Task.Run(() => sent.WaitOne(Timeout)));
            await ObserveCancellationWaitAsync(child, probe);
            probe.Set("child-release");
            probe.Set("parent-release");
            await exited.WaitAsync(Timeout);
            Assert.Equal(0, helper.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(Timeout));
            string json = await File.ReadAllTextAsync(receipt);
            testOutput.WriteLine(json);
            using var document = JsonDocument.Parse(json);
            JsonElement result = document.RootElement;
            Assert.Equal("PASS", result.GetProperty("result").GetString());
            Assert.Equal(mode, result.GetProperty("mode").GetString());
            Assert.Equal(pid, result.GetProperty("child_pid").GetInt32());
            Assert.Equal(130, result.GetProperty("code").GetInt32());
            Assert.True(result.GetProperty("natural_exit").GetBoolean());
            Assert.Equal("owned-console-only",
                result.GetProperty("console_scope").GetString());
            Assert.Equal(0, result.GetProperty("console_workers_remaining").GetInt32());
            Assert.Equal(Hash(cli), result.GetProperty("cli_sha256").GetString());
            Assert.Equal(Hash(Path.ChangeExtension(cli, ".dll")),
                result.GetProperty("cli_assembly_sha256").GetString());
            Assert.Equal(CancellationOutput(externalCall),
                result.GetProperty("stdout").GetString());
            AssertCancellationEvents(probe);
        }
        finally
        {
            probe.Set("child-release");
            probe.Set("parent-release");
            await FinishCancellationProcessAsync(helper, exited);
            if (helper.ExitCode != 0 && File.Exists(receipt))
            {
                testOutput.WriteLine(await File.ReadAllTextAsync(receipt));
            }
            testOutput.WriteLine(JsonSerializer.Serialize(new
            {
                mode,
                externalCall,
                events = probe.Snapshot(),
                fixture_sha256 = Hash(probe.Library),
                console_helper_sha256 = Hash(fixture),
                console_helper_assembly_sha256 = Hash(
                    Path.ChangeExtension(fixture, ".dll")),
                stdout = await stdout.WaitAsync(Timeout),
                stderr = await stderr.WaitAsync(Timeout)
            }));
        }
    }

    [Theory]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("close", false)]
    [InlineData("close", true)]
    public async Task WpfCancellationRetainsNativeCalls(string mode,
        bool externalCall)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var probe = new Probe(externalCall, exit: false);
        string executable = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        using Process process = StartCancellationProcess(executable,
            probe.Directory, [probe.Main]);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task exited = process.WaitForExitAsync();
        try
        {
            await WaitForBlockedCallsAsync(process, probe);
            nint window = FindOwnedMainWindow(process.Id);
            if (mode == "stop")
            {
                using var cancellation = new CancellationTokenSource(Timeout);
                Assert.True(await UiaItemActions.InvokeAsync(window, "■ 停止",
                    (int)(ClickItemFlags.Button | ClickItemFlags.Background),
                    1, 1, cancellation.Token).WaitAsync(Timeout));
            }
            else
            {
                Assert.True(Native.PostMessageW(window, 0x0010, 0, 0));
            }
            await ObserveCancellationWaitAsync(process, probe);
            probe.Set("child-release");
            probe.Set("parent-release");
            await exited.WaitAsync(Timeout);
            Assert.Equal(130, process.ExitCode);
            Assert.Equal(CancellationOutput(externalCall),
                (await stdout.WaitAsync(Timeout)).Replace("\r\n", "\n",
                    StringComparison.Ordinal));
            Assert.Equal("", await stderr.WaitAsync(Timeout));
            AssertCancellationEvents(probe);
        }
        finally
        {
            probe.Set("child-release");
            probe.Set("parent-release");
            await FinishCancellationProcessAsync(process, exited);
            testOutput.WriteLine(JsonSerializer.Serialize(new
            {
                mode,
                externalCall,
                code = process.ExitCode,
                stdout = await stdout.WaitAsync(Timeout),
                stderr = await stderr.WaitAsync(Timeout),
                events = probe.Snapshot(),
                executable_sha256 = Hash(executable),
                frontend_assembly_sha256 = Hash(
                    Path.ChangeExtension(executable, ".dll")),
                fixture_sha256 = Hash(probe.Library)
            }));
        }
    }

    private static Process StartCancellationProcess(string executable,
        string directory, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start)
            ?? throw new InvalidOperationException("検証プロセスを開始できません。");
    }

    private static async Task WaitForBlockedCallsAsync(Process process, Probe probe)
    {
        await Task.WhenAll(probe.WaitAsync("child-entered"),
            probe.WaitAsync("parent-entered")).WaitAsync(Timeout);
        Assert.False(process.HasExited);
        process.Refresh();
        Assert.Single(process.Modules.Cast<ProcessModule>(), module =>
            string.Equals(module.FileName, probe.Library,
                StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ObserveCancellationWaitAsync(Process process,
        Probe probe, bool parentBlocked = true)
    {
        var observation = Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Same(observation,
            await Task.WhenAny(process.WaitForExitAsync(), observation));
        Assert.False(probe.IsSet("child-verified"));
        Assert.Equal(!parentBlocked, probe.IsSet("parent-verified"));
        Assert.False(probe.IsSet("explicit-unload"));
        Assert.False(probe.IsSet("process-unload"));
    }

    private static void AssertCancellationEvents(Probe probe)
    {
        Assert.True(probe.IsSet("child-verified"));
        Assert.True(probe.IsSet("parent-verified"));
        Assert.True(probe.IsSet("explicit-unload"));
        Assert.False(probe.IsSet("process-unload"));
    }

    private static string CancellationOutput(bool externalCall) =>
        "watch=1\n" + (externalCall ? "call-returned\n" : "")
            + "worker-finally=101,3\n";

    private static async Task FinishCancellationProcessAsync(Process process,
        Task exited)
    {
        if (!process.HasExited)
        {
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2)));
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await exited.WaitAsync(Timeout);
        }
    }

    private static nint FindOwnedMainWindow(int processId)
    {
        var matches = new List<nint>();
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)processId && Native.IsWindowVisible(window))
            {
                var name = new StringBuilder(512);
                Native.GetWindowTextW(window, name, name.Capacity);
                if (name.ToString().Contains("記録・一時停止", StringComparison.Ordinal))
                {
                    matches.Add(window);
                }
            }
            return true;
        }, 0);
        return Assert.Single(matches);
    }
}
