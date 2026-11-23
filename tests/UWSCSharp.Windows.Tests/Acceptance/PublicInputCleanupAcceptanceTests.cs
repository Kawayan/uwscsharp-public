using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class PublicInputCleanupAcceptanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("missing-parent")]
    [InlineData("directory")]
    public async Task InvalidReceiptPathExitsWithADiagnostic(string variant)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-public-cleanup-path-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string receipt = variant == "directory" ? Path.Combine(directory, "as-file")
            : Path.Combine(directory, "missing", "receipt.json");
        if (variant == "directory")
        {
            Directory.CreateDirectory(receipt);
        }
        var start = new ProcessStartInfo(FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--verify-input-cleanup-public");
        start.ArgumentList.Add(receipt);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine(await stdout);
            string diagnostic = await stderr;
            output.WriteLine(diagnostic);
            Assert.Equal(1, process.ExitCode);
            Assert.False(File.Exists(receipt));
            Assert.False(string.IsNullOrWhiteSpace(diagnostic));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                variant,
                exit_code = process.ExitCode,
                receipt_created = false
            }));
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert.StartsWith(temp, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WpfPublicScriptUsesTheManualRecoveryButtonAndRunsAgain()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string executable = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        string path = Path.Combine(Path.GetTempPath(), "uwsc-public-cleanup-" + Guid.NewGuid() + ".json");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--verify-input-cleanup-exit-dialog");
        start.ArgumentList.Add(path);
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
            Assert.True(File.Exists(path), "検証結果が生成されていません。");
            string text = await File.ReadAllTextAsync(path);
            output.WriteLine(text);
            using var json = JsonDocument.Parse(text);
            JsonElement result = json.RootElement;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("PASS", result.GetProperty("result").GetString());
            Assert.True(result.GetProperty("window_shown").GetBoolean());
            Assert.False(result.GetProperty("input_injection").GetBoolean());
            JsonElement[] checks = [.. result.GetProperty("checks").EnumerateArray()];
            Assert.Equal(3, checks.Length);
            Assert.Equal([0, 1, 2], checks.Select(check => check.GetProperty("mode").GetInt32()));
            foreach (JsonElement check in checks)
            {
                Assert.Equal(1, check.GetProperty("failed_code").GetInt32());
                Assert.Equal(2, check.GetProperty("blocked_code").GetInt32());
                Assert.Equal(0, check.GetProperty("rerun_code").GetInt32());
                Assert.Equal(0, check.GetProperty("pending").GetInt32());
                bool checksExitDialog = check.GetProperty("mode").GetInt32() == 0;
                Assert.Equal(checksExitDialog,
                    check.GetProperty("exit_dialog_declined").GetBoolean());
                Assert.Equal(checksExitDialog,
                    check.GetProperty("exit_dialog_accepted").GetBoolean());
                Assert.Equal(3, check.GetProperty("initial_stops").GetInt32());
                Assert.Equal(1, check.GetProperty("rerun_stops").GetInt32());
                Assert.True(check.GetProperty("transferred_run_blocked_and_restored").GetBoolean());
                Assert.True(check.GetProperty("named_pipe").GetBoolean());
                Assert.True(check.GetProperty("transferred_run_blocked_during_recovery").GetBoolean());
                Assert.True(check.GetProperty("diagnostic_preserved").GetBoolean());
                Assert.True(check.GetProperty("starts_restored").GetBoolean());
            }
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            File.Delete(path);
        }
    }
}
