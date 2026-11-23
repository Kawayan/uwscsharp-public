using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class CliInputCleanupAcceptanceTests(ITestOutputHelper output)
{
    // 専用コンソールの入力・制御信号を使うためHeadlessには分類しません。
    [Fact]
    public async Task OwnedConsoleVerifiesManualInputCancellationAndRestoration()
        => await VerifyOwnedConsoleAsync();

    internal async Task VerifyOwnedConsoleAsync(string? publishedCli = null)
    {
        string root = FindRoot();
        string configuration = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string fixture = Path.Combine(root, "artifacts", "bin", "UWSCSharp.ControlFixture", configuration, "UWSCSharp.ControlFixture.exe");
        string cli = publishedCli ?? Path.Combine(root, "artifacts", "bin",
            "UWSCSharp.Cli", configuration, "UWSCSharp.Cli.exe");
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-cli-recovery-acceptance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string receipt = Path.Combine(directory, "receipt.json");
        var start = new ProcessStartInfo(fixture)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--cli-input-cleanup-probe");
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add(receipt);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
            Assert.True(File.Exists(receipt));
            string json = await File.ReadAllTextAsync(receipt);
            output.WriteLine(json);
            using var result = JsonDocument.Parse(json);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("PASS", result.RootElement.GetProperty("result").GetString());
            Assert.Equal("owned-console-only", result.RootElement.GetProperty("console_scope").GetString());
            Assert.Equal(28, result.RootElement.GetProperty("checks").GetArrayLength());
            foreach (int mode in new[] { 0, 1, 2 })
            {
                JsonElement check = Assert.Single(result.RootElement.GetProperty("checks").EnumerateArray(),
                    entry => entry.GetProperty("name").GetString() == $"public-script-{mode}");
                Assert.Equal(1, check.GetProperty("code").GetInt32());
                Assert.Equal(0, check.GetProperty("pending").GetInt32());
                Assert.Equal(1, check.GetProperty("prompts").GetInt32());
                Assert.Equal(mode == 0 ? 3 : mode, check.GetProperty("registered_devices").GetInt32());
                Assert.Equal(3, check.GetProperty("stops").GetInt32());
                Assert.True(check.GetProperty("restored").GetBoolean());
                foreach (string variant in new[] { "retry", "exit", "cancel-wait", "cancel-retry" })
                {
                    JsonElement application = Assert.Single(
                        result.RootElement.GetProperty("checks").EnumerateArray(),
                        entry => entry.GetProperty("name").GetString() == $"application-script-{mode}-{variant}");
                    bool recovered = variant is "retry" or "cancel-retry";
                    Assert.Equal(variant.StartsWith("cancel-", StringComparison.Ordinal) ? 130 : 1,
                        application.GetProperty("code").GetInt32());
                    Assert.Equal(recovered ? 0 : 1, application.GetProperty("pending").GetInt32());
                    Assert.Equal(recovered ? 3 : 2, application.GetProperty("stops").GetInt32());
                    Assert.Equal(mode == 0 ? 3 : mode, application.GetProperty("registered_devices").GetInt32());
                    Assert.Equal(1, application.GetProperty("prompts").GetInt32());
                    Assert.True(application.GetProperty("restored").GetBoolean());
                }
            }
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("other")]
    public async Task RedirectedOrUnknownManualLaunchIsRejectedBeforeScript(string mode)
    {
        string root = FindRoot();
        string configuration = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string cli = Path.Combine(root, "artifacts", "bin", "UWSCSharp.Cli", configuration, "UWSCSharp.Cli.exe");
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-cli-recovery-reject-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "must-not-run.uws");
        string marker = Path.Combine(directory, "executed.txt");
        await File.WriteAllTextAsync(script, "DIM f=FOPEN(PARAM_STR[0],F_WRITE8)\nFPUT(f,\"executed\")\nFCLOSE(f)\n");
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--input-cleanup=" + mode);
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(marker);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, process.ExitCode);
            Assert.Equal("", await stdout);
            Assert.Contains("起動設定", await stderr);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "UWSCSharp.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new InvalidOperationException("作業ルートがありません。");
    }
}
