using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 明示的に配備した配布用CLIのconsole受入を行います。
/// </summary>
[Trait("Category", "SandboxOnly")]
public sealed class PublishedCliM1AcceptanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ctrl-c", false)]
    [InlineData("break", false)]
    [InlineData("ctrl-c", true)]
    [InlineData("break", true)]
    public async Task PublishedCliPassesOwnedConsoleContracts(
        string mode, bool worker)
        => await VerifyPublishedCliAsync("M1CliRelease", mode, worker);

    internal async Task VerifyPublishedCliAsync(
        string project, string mode, bool worker)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        string cli = FixturePaths.Get(project, "UWSCSharp.Cli.exe");
        string assembly = Path.ChangeExtension(cli, ".dll");
        Assert.True(File.Exists(fixture), fixture);
        Assert.True(File.Exists(cli), cli);
        Assert.True(File.Exists(assembly), assembly);
        string apphostHash = Hash(cli);
        string assemblyHash = Hash(assembly);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-published-cli-m1-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string receipt = Path.Combine(directory, "result.json");
        var start = new ProcessStartInfo(fixture)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(worker
            ? "--cli-thread-console-probe" : "--cli-console-probe");
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
            output.WriteLine(await stdout.WaitAsync(TimeSpan.FromSeconds(12)));
            output.WriteLine(await stderr.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.True(File.Exists(receipt), "配布CLIの検証結果がありません。");
            string json = await File.ReadAllTextAsync(receipt);
            output.WriteLine(json);
            using var document = JsonDocument.Parse(json);
            JsonElement result = document.RootElement;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("PASS", result.GetProperty("result").GetString());
            Assert.Equal(mode, result.GetProperty("mode").GetString());
            Assert.Equal("owned-console-only",
                result.GetProperty("console_scope").GetString());
            Assert.Equal(apphostHash,
                result.GetProperty("cli_sha256").GetString());
            Assert.Equal(assemblyHash,
                result.GetProperty("cli_assembly_sha256").GetString());
            Assert.Equal(apphostHash, Hash(cli));
            Assert.Equal(assemblyHash, Hash(assembly));
            JsonElement checks = result.GetProperty("checks");
            if (worker)
            {
                VerifyWorkers(checks);
            }
            else
            {
                VerifyMain(checks);
            }
        }
        finally
        {
            if (started && !process.HasExited)
            {
                // 期限超過時の所有process回収を成功とは扱いません。
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(12));
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyMain(JsonElement checks)
    {
        string[] names =
        [
            "default", "optfinally", "cleanup-error", "cleanup-timeout",
            "empty-while", "empty-repeat", "empty-for", "cleanup-step-limit",
            "save-failure", "sleep-maximum", "usage", "missing-source",
            "source-directory", "runtime-error", "explicit-exit",
            "write-directory"
        ];
        Assert.Equal(16, checks.GetArrayLength());
        for (int index = 0; index < names.Length; index++)
        {
            JsonElement check = checks[index];
            Assert.Equal(names[index], check.GetProperty("name").GetString());
            int expectedCode = index < 10 ? 130
                : index == 10 ? 2 : index == 14 ? 37 : 1;
            Assert.Equal(expectedCode,
                check.GetProperty("exit_code").GetInt32());
            if (index < 10)
            {
                Assert.Equal(index != 0,
                    check.GetProperty("cleanup").GetBoolean());
                Assert.True(check.GetProperty("rerun_and_reopen").GetBoolean());
            }
        }
    }

    private static void VerifyWorkers(JsonElement checks)
    {
        string[] names =
            ["thread-default", "thread-optfinally", "worker-exit37"];
        Assert.Equal(3, checks.GetArrayLength());
        for (int index = 0; index < names.Length; index++)
        {
            JsonElement check = checks[index];
            Assert.Equal(names[index], check.GetProperty("name").GetString());
            Assert.Equal(index == 2 ? 37 : 130,
                check.GetProperty("exit_code").GetInt32());
            Assert.Equal(index != 0,
                check.GetProperty("cleanup").GetBoolean());
            Assert.Equal(index != 2,
                check.GetProperty("signal_sent").GetBoolean());
            Assert.Equal(["MAIN_READY", "WORKER_READY"],
                [.. check.GetProperty("ready").EnumerateArray()
                    .Select(entry => entry.GetString()!)]);
            Assert.True(check.GetProperty("main_buffer_saved").GetBoolean());
            Assert.True(check.GetProperty("worker_buffer_saved").GetBoolean());
            Assert.True(check.GetProperty("natural_exit").GetBoolean());
            Assert.Equal(0,
                check.GetProperty("console_workers_remaining").GetInt32());
            Assert.True(check.GetProperty("rerun_and_reopen").GetBoolean());
        }
    }

    private static string Hash(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path)));
}
