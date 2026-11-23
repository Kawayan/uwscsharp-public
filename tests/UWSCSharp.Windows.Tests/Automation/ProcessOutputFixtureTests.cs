using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ProcessOutputFixtureTests
{
    [Fact]
    public async Task BothPipesDrainBeforeWaitingForExit()
    {
        const int size = 1024 * 1024;
        using Process process = Start("duplex", size);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<byte[]> output = ProcessOutputReader.ReadAsync(process.StandardOutput.BaseStream, size, timeout.Token);
            Task<byte[]> error = ProcessOutputReader.ReadAsync(process.StandardError.BaseStream, size, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            byte[] capturedOutput = await output;
            byte[] capturedError = await error;
            Assert.Equal(size, capturedOutput.Length);
            Assert.Equal(size, capturedError.Length);
            Assert.True(capturedOutput.All(value => value == 65));
            Assert.True(capturedError.All(value => value == 90));
            Assert.Equal(37, process.ExitCode);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp932")]
    [InlineData("invalid")]
    public async Task ProducerBytesAreCapturedWithoutTextConversion(string mode)
    {
        using Process process = Start(mode, 0);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            byte[] output = await ProcessOutputReader.ReadAsync(process.StandardOutput.BaseStream, 1000, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] expected = mode switch
            {
                "utf16" => new UnicodeEncoding(false, false, true).GetBytes("日本語😀\r\nsecond\0tail"),
                "cp932" => Encoding.GetEncoding(932).GetBytes("日本語\r\nsecond\0tail"),
                "invalid" => [0, 0xf0, 0x9f, 0x98, 0xff],
                _ => Encoding.UTF8.GetBytes("日本語😀\r\nsecond\0tail")
            };
            Assert.Equal(expected, output);
            Assert.Equal(37, process.ExitCode);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task ReaderLimitIsReportedWhileTheProducerIsStillRunning()
    {
        using Process process = Start("stdout", 1024 * 1024);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ProcessOutputLimitException error = await Assert.ThrowsAsync<ProcessOutputLimitException>(() =>
                ProcessOutputReader.ReadAsync(process.StandardOutput.BaseStream, 1024, timeout.Token));
            Assert.Equal(1024, error.MaximumBytes);
            Assert.Equal(1025, error.ObservedBytes);
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task ProducerPublishesCompletionOnlyAfterTheOutputIsDrained()
    {
        string marker = Path.Combine(Path.GetTempPath(),
            "uwsc output marker " + Guid.NewGuid().ToString("N") + ".txt");
        using Process process = Start("stdout", 1024 * 1024, marker);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker + ".started"))
            {
                await Task.Delay(25, timeout.Token);
            }

            Assert.False(File.Exists(marker));
            Assert.False(process.HasExited);
            Assert.Equal(process.Id,
                await ProcessReceiptReader.ReadPidAsync(marker + ".started", timeout.Token));
            byte[] bytes = await ProcessOutputReader.ReadAsync(
                process.StandardOutput.BaseStream, 1024 * 1024, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1024 * 1024, bytes.Length);
            Assert.True(bytes.All(value => value == 65));
            Assert.Equal(process.Id,
                await ProcessReceiptReader.ReadPidAsync(marker, timeout.Token));
            Assert.Equal(37, process.ExitCode);
        }
        finally
        {
            await StopAsync(process);
            foreach (string path in new[] { marker, marker + ".started",
                marker + ".pending", marker + ".started.pending" })
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task GatedProducerWaitsForAcknowledgementBeforeWriting()
    {
        string marker = Path.Combine(Path.GetTempPath(),
            "uwsc gated output " + Guid.NewGuid().ToString("N") + ".txt");
        using Process process = Start("gated", 0, marker);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<byte[]> read = ProcessOutputReader.ReadAsync(
                process.StandardOutput.BaseStream, 100, timeout.Token);
            while (!File.Exists(marker + ".started"))
            {
                await Task.Delay(25, timeout.Token);
            }

            Assert.False(read.IsCompleted);
            Assert.False(File.Exists(marker));
            Assert.False(process.HasExited);
            await File.WriteAllTextAsync(marker + ".go.pending", "ready", timeout.Token);
            File.Move(marker + ".go.pending", marker + ".go");
            Assert.Equal("late-output"u8.ToArray(), await read);
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(File.Exists(marker));
            Assert.False(File.Exists(marker + ".write-error"));
            Assert.Equal(37, process.ExitCode);
        }
        finally
        {
            await StopAsync(process);
            foreach (string suffix in new[] { "", ".started", ".pending",
                ".started.pending", ".go", ".go.pending", ".write-error" })
            {
                File.Delete(marker + suffix);
            }
        }
    }

    private static Process Start(string mode, int size, string? marker = null)
    {
        var start = new ProcessStartInfo(FixturePaths.Get(
            "UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "--process-output-probe", mode,
            size.ToString(CultureInfo.InvariantCulture), "37" })
        {
            start.ArgumentList.Add(argument);
        }

        if (marker is not null)
        {
            start.ArgumentList.Add(marker);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("検証用プロセスを起動できません。");
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
