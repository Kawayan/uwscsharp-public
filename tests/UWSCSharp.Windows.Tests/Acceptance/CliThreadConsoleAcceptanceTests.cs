using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class CliThreadConsoleAcceptanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ctrl-c")]
    [InlineData("break")]
    public async Task OwnedConsoleStopsAndJoinsWorkersAndSavesTheirBuffers(
        string mode)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        Assert.Contains(configuration, new[] { "debug", "release" });
        string fixture = Path.Combine(root.FullName, "artifacts", "bin",
            "UWSCSharp.ControlFixture", configuration,
            "UWSCSharp.ControlFixture.exe");
        string cli = Path.Combine(root.FullName, "artifacts", "bin",
            "UWSCSharp.Cli", configuration, "UWSCSharp.Cli.exe");
        Assert.True(File.Exists(fixture), fixture);
        Assert.True(File.Exists(cli), cli);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-thread-console-acceptance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string receipt = Path.Combine(directory, "result.json");
        var start = new ProcessStartInfo(fixture)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--cli-thread-console-probe");
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(receipt);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(90));
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
            Assert.True(File.Exists(receipt), "THREAD検証結果がありません。");
            string json = await File.ReadAllTextAsync(receipt);
            output.WriteLine(json);
            using var document = JsonDocument.Parse(json);
            JsonElement result = document.RootElement;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("PASS", result.GetProperty("result").GetString());
            Assert.Equal(mode, result.GetProperty("mode").GetString());
            Assert.Equal("owned-console-only",
                result.GetProperty("console_scope").GetString());
            Assert.Equal(64, result.GetProperty("cli_sha256").GetString()!.Length);
            Assert.Equal(64, result.GetProperty("cli_assembly_sha256")
                .GetString()!.Length);
            JsonElement checks = result.GetProperty("checks");
            Assert.Equal(3, checks.GetArrayLength());
            for (int index = 0; index < checks.GetArrayLength(); index++)
            {
                JsonElement check = checks[index];
                Assert.Equal(index == 2 ? 37 : 130,
                    check.GetProperty("exit_code").GetInt32());
                Assert.Equal(index != 0,
                    check.GetProperty("cleanup").GetBoolean());
                Assert.Equal(index != 2,
                    check.GetProperty("signal_sent").GetBoolean());
                Assert.Equal(["MAIN_READY", "WORKER_READY"],
                    [.. check.GetProperty("ready").EnumerateArray().Select(entry => entry.GetString()!)]);
                Assert.True(check.GetProperty("main_buffer_saved").GetBoolean());
                Assert.True(check.GetProperty("worker_buffer_saved").GetBoolean());
                Assert.True(check.GetProperty("natural_exit").GetBoolean());
                Assert.Equal(0,
                    check.GetProperty("console_workers_remaining").GetInt32());
                Assert.True(check.GetProperty("rerun_and_reopen").GetBoolean());
            }
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(12));
            }

            Directory.Delete(directory, recursive: true);
        }
    }
}
