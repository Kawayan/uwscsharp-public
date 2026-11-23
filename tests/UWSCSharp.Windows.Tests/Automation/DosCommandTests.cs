using System.Diagnostics;
using System.Globalization;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class DosCommandTests
{
    [Theory]
    [InlineData("echo hello", "hello\r\n")]
    [InlineData("", "")]
    [InlineData("exit /b 37", "")]
    [InlineData("echo one\r\n\n echo two", "one \r\ntwo\r\n")]
    public async Task CapturesTextWithoutTrimmingOrTreatingExitCodeAsFailure(
        string command, string expected)
    {
        Assert.Equal(expected, (await InvokeAsync(command)).Text);
    }

    [Fact]
    public async Task QuotedPathPreservesShellMetacharactersInRedirection()
    {
        DirectoryInfo directory =
            Directory.CreateTempSubdirectory("uwsc command ");
        try
        {
            string path = Path.Combine(directory.FullName, "日本語 & (確認).txt");
            string command = $"echo first>\"{path}\"&&echo second>>\"{path}\""
                + $"&&type \"{path}\"";
            Value result = await InvokeAsync(command);
            Assert.Equal(Value.String("first\r\nsecond\r\n"), result);
            Assert.Equal(result.Text, await File.ReadAllTextAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("日本語", 1)]
    [InlineData("日本語😀", 1)]
    [InlineData("日本語😀", 5)]
    public async Task UnicodeModeDecodesOutputWrittenByCmd(string text, int mode)
    {
        Value result = await InvokeAsync("echo " + text,
            new WindowsCommandOptions { AnsiCodePage = 932 }, mode);

        Assert.Equal(Value.String(text + "\r\n"), result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(259)]
    [InlineData(-1)]
    public async Task ExitCodeModePreservesSignedInt32Metadata(int code)
    {
        Value result = await InvokeAsync("exit /b "
            + code.ToString(CultureInfo.InvariantCulture), mode: 2);
        Assert.Equal(code, result.Number);
        Assert.Equal((ushort)3, result.NativeVariantType);
        Assert.Equal(code, result.GetUnchangedNativeScalar());
    }

    [Theory]
    [InlineData(7900, 37)]
    [InlineData(9000, 1)]
    public async Task ExitCodeModeReportsShellCommandLineLengthResult(
        int commentLength, int expected)
    {
        string command = "exit /b 37 & rem " + new string('x', commentLength);
        Value result = await InvokeAsync(command, mode: 2);

        Assert.Equal(expected, result.Number);
        Assert.Equal((ushort)3, result.NativeVariantType);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task ExitCodeModeIgnoresOutputLimitAndEncoding(int mode)
    {
        var options = new WindowsCommandOptions
        {
            MaximumOutputBytes = 1024,
            AnsiCodePage = 65001
        };
        Value large = await InvokeAsync(Command("stdout", 65_537),
            options, mode);
        Value invalid = await InvokeAsync(Command("invalid"), options, mode);
        Assert.Equal(37, large.Number);
        Assert.Equal(37, invalid.Number);
        Assert.Equal((ushort)3, large.NativeVariantType);
        Assert.Equal((ushort)3, invalid.NativeVariantType);
    }

    [Theory]
    [InlineData("ordered", 932, 0, "stdout-1|stderr-1|stdout-2|stderr-2")]
    [InlineData("stdin", 932, 0, "\u0001")]
    [InlineData("cp932", 932, 0, "日本語\r\nsecond\0tail")]
    [InlineData("cp932", 932, 4, "日本語\r\nsecond\0tail")]
    [InlineData("utf8", 65001, 0, "日本語😀\r\nsecond\0tail")]
    [InlineData("utf16", 932, 1, "日本語😀\r\nsecond\0tail")]
    public async Task ControlledOutputPreservesOrderEncodingAndNul(
        string producer, int codePage, int mode, string expected)
    {
        Assert.Equal(expected, (await InvokeAsync(Command(producer),
            new WindowsCommandOptions { AnsiCodePage = codePage }, mode)).Text);
    }

    [Theory]
    [InlineData("invalid", 1)]
    [InlineData("invalid", 0)]
    [InlineData("cp932", 1)]
    public async Task UndecodableOutputFailsInsteadOfReturningPartialText(
        string producer, int mode)
    {
        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            InvokeAsync(Command(producer),
                new WindowsCommandOptions { AnsiCodePage = 65001 }, mode));
    }

    [Fact]
    public async Task ExactLimitSucceedsAndOverflowIsCatchable()
    {
        var options = new WindowsCommandOptions { MaximumOutputBytes = 1024 };
        Assert.Equal(new string('A', 1024),
            (await InvokeAsync(Command("stdout", 1024), options)).Text);
        ProcessOutputLimitException error = await Assert.ThrowsAsync<ProcessOutputLimitException>(
            () => InvokeAsync(Command("stdout", 1025), options));
        Assert.Equal(1025, error.ObservedBytes);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation { Commands = options })
            .RunAsync("TRY\nDOSCMD(\"echo " + new string('x', 1100)
                + "\")\nEXCEPT\nPRINT \"caught\"\nENDTRY", output.Add);
        Assert.Equal(["caught"], output);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("parent-wait")]
    public async Task CancellationStopsDescendantEvenAfterProducerExit(string producer)
    {
        string marker = Marker();
        using var cancel = new CancellationTokenSource();
        Task<Value> running = InvokeAsync(Command(producer, marker: marker), token: cancel.Token);
        try
        {
            int id = await WaitMarkerAsync(marker);
            var child = Process.GetProcessById(id);
            _ = child.SafeHandle;
            Assert.False(child.HasExited);
            Assert.False(running.IsCompleted);
            Task observation = ObserveExitAsync(child);
            cancel.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.Empty(error.Data);
            await observation;
        }
        finally
        {
            cancel.Cancel();
            await Record.ExceptionAsync(() => running);
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task CaptureWaitsForLateDescendantOutput()
    {
        string marker = Marker();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string command = "start \"\" /b " + Command("gated", marker: marker);
        Task<Value> running = InvokeAsync(command, token: timeout.Token);
        try
        {
            await WaitMarkerAsync(marker + ".started");
            await Task.Delay(150, timeout.Token);
            Assert.False(running.IsCompleted);
            await File.WriteAllTextAsync(marker + ".go", "go", timeout.Token);
            Assert.Equal("late-output", (await running).Text);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            timeout.Cancel();
            await Record.ExceptionAsync(() => running);
            DeleteMarkers(marker);
        }
    }

    [Theory]
    [InlineData("invalid-parent")]
    [InlineData("overflow-parent")]
    public async Task AcquisitionFailureStopsDescendantWithIndependentOutput(string producer)
    {
        string marker = Marker();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<Value> running = InvokeAsync(Command(producer, 4096, marker),
            new WindowsCommandOptions { AnsiCodePage = 65001, MaximumOutputBytes = 100 },
            token: cancel.Token);
        try
        {
            int id = await WaitMarkerAsync(marker);
            var child = Process.GetProcessById(id);
            _ = child.SafeHandle;
            Task observation = ObserveExitAsync(child);
            await File.WriteAllTextAsync(marker + ".go", "go");
            Exception error = (await Record.ExceptionAsync(() => running))!;
            Assert.NotNull(error);
            if (producer == "invalid-parent")
            {
                Assert.IsType<DecoderFallbackException>(error);
            }
            else
            {
                Assert.IsType<ProcessOutputLimitException>(error);
            }

            Assert.Empty(error.Data);
            await observation;
        }
        finally
        {
            cancel.Cancel();
            await Record.ExceptionAsync(() => running);
            DeleteMarkers(marker);
        }
    }

    [Fact]
    public void HostConfigurationIsValidatedBeforeAnyLaunch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsAutomation
            {
                Commands = new WindowsCommandOptions { MaximumOutputBytes = -1 }
            }.CreateSession());
        Assert.ThrowsAny<ArgumentException>(() =>
            new WindowsAutomation
            {
                Commands = new WindowsCommandOptions { AnsiCodePage = 999999 }
            }.CreateSession());
    }

    [Fact]
    public async Task NoWaitLeavesControlledChildAliveWithNullInput()
    {
        string marker = Marker();
        Process? child = null;
        try
        {
            Value result = await InvokeAsync(Command("gated", marker: marker), noWait: true);
            Assert.Equal("", result.Text);
            int id = await WaitMarkerAsync(marker + ".started");
            child = Process.GetProcessById(id);
            _ = child.SafeHandle;
            Assert.False(child.HasExited);
            await File.WriteAllTextAsync(marker + ".go", "go");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(37, child.ExitCode);
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync();
                }

                child.Dispose();
            }

            DeleteMarkers(marker);
        }
    }

    private static async Task<Value> InvokeAsync(string command,
        WindowsCommandOptions? options = null, int mode = 0,
        bool noWait = false, CancellationToken token = default)
    {
        await using IWindowsAutomationSession session = new WindowsAutomation
        {
            Commands = options ?? new WindowsCommandOptions()
        }.CreateSession();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await session.InvokeAsync(BuiltinFunctionNames.DosCmd,
            [Value.String(command), Value.Numeric(noWait ? 1 : 0),
                Value.Numeric(0), Value.Numeric(mode)], token.CanBeCanceled ? token : timeout.Token);
    }

    private static string Command(string mode, int size = 0, string? marker = null)
    {
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        return $"\"{executable}\" --process-output-probe {mode} {size} 37"
            + (marker is null ? "" : $" \"{marker}\"");
    }

    private static string Marker() => Path.Combine(Path.GetTempPath(),
        "uwsc command marker " + Guid.NewGuid().ToString("N") + ".txt");

    private static async Task<int> WaitMarkerAsync(string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(marker))
        {
            await Task.Delay(25, timeout.Token);
        }

        return await ProcessReceiptReader.ReadPidAsync(marker, timeout.Token);
    }

    private static async Task ObserveExitAsync(Process child)
    {
        using (child)
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, child.ExitCode);
        }
    }

    private static void DeleteMarkers(string marker)
    {
        foreach (string path in new[] { marker, marker + ".started",
            marker + ".go", marker + ".write-error" })
        {
            File.Delete(path);
        }
    }
}
