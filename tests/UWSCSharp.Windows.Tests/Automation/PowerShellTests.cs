using System.Diagnostics;
using System.Globalization;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class PowerShellTests
{
    [Theory]
    [InlineData("'日本語😀'", "日本語😀\r\n")]
    [InlineData("[Console]::Out.Write('A'+[char]0+'B')", "A\0B")]
    [InlineData("[Console]::Out.Write('out|');[Console]::Error.Write('err|')", "out|err|")]
    [InlineData("[Console]::OpenStandardInput().ReadByte()", "-1\r\n")]
    [InlineData("exit 37", "")]
    [InlineData("", "")]
    [InlineData("'<Objs>literal</Objs>'", "<Objs>literal</Objs>\r\n")]
    [InlineData("'C:\\space path\\';'say \"日本語😀\"';'it''s literal'",
        "C:\\space path\\\r\nsay \"日本語😀\"\r\nit's literal\r\n")]
    [InlineData("$name='日本語😀'\r\n@\"\r\n\"$name\"\\\r\nsecond\r\n\"@",
        "\"日本語😀\"\\\r\nsecond\r\n")]
    [InlineData("[Console]::OpenStandardOutput().Write([byte[]](239,187,191,65,0,66),0,6)", "A\0B")]
    public async Task PublicFunctionReturnsStrictUtf8Text(string command, string expected)
    {
        Value result = await InvokeAsync(command);
        Assert.Equal(ValueKind.String, result.Kind);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public async Task DecoderAndLimitErrorsAreCatchableWithoutPartialSuccess()
    {
        var options = new WindowsCommandOptions { MaximumOutputBytes = 1024 };
        Assert.Equal(new string('A', 1024),
            (await InvokeAsync("[Console]::Out.Write('A'*1024)", options)).Text);
        await Assert.ThrowsAsync<ProcessOutputLimitException>(() =>
            InvokeAsync("[Console]::Out.Write('A'*1025)", options));
        await Assert.ThrowsAsync<DecoderFallbackException>(() =>
            InvokeAsync("[Console]::OpenStandardOutput().Write([byte[]](255),0,1)"));
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            "TRY\nPOWERSHELL(\"[Console]::OpenStandardOutput().Write([byte[]](255),0,1)\")"
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY", output.Add);
        Assert.Equal(["caught"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultLimitCountsBothOutputStreams(bool exceedLimit)
    {
        const int halfLimit = 8 * 1024 * 1024;
        const string command = "[Console]::Out.Write('A'*8MB);"
            + "[Console]::Error.Write('B'*8MB)";
        if (exceedLimit)
        {
            await Assert.ThrowsAsync<ProcessOutputLimitException>(() =>
                InvokeAsync(command + ";[Console]::Error.Write('!')"));
            return;
        }

        Value result = await InvokeAsync(command);
        Assert.Equal(ValueKind.String, result.Kind);
        Assert.Equal(2 * halfLimit, result.Text.Length);
        Assert.Equal(-1, result.Text.AsSpan(0, halfLimit).IndexOfAnyExcept('A'));
        Assert.Equal(-1, result.Text.AsSpan(halfLimit).IndexOfAnyExcept('B'));
    }

    [Fact]
    public async Task StartupUsesObservedEngineAndApprovedFlags()
    {
        string output = (await InvokeAsync(
            "[Environment]::Is64BitProcess;[Environment]::CommandLine;[Console]::OutputEncoding.CodePage")).Text;
        Assert.StartsWith("False\r\n", output, StringComparison.Ordinal);
        Assert.Contains("-NoProfile -NonInteractive -OutputFormat Text -Command", output);
        Assert.EndsWith("65001\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyntaxErrorsKeepJapaneseAndNonBmpAsReadableText()
    {
        string result = (await InvokeAsync("'日本語😀' {")).Text;
        Assert.Contains("日本語😀", result);
        Assert.DoesNotContain("CLIXML", result);
        Assert.DoesNotContain("FromBase64String", result);
    }

    [Fact]
    public async Task RuntimeParsingDoesNotAddLocalScopeOrCatchUserStatements()
    {
        Assert.Equal("42\r\n", (await InvokeAsync(
            "$probeValue=42;Get-Variable -Name probeValue -Scope Global -ValueOnly")).Text);
        string output = (await InvokeAsync("Read-Host 'prompt';'after-prompt'")).Text;
        Assert.EndsWith("after-prompt\r\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpandedCommandLengthIsScriptErrorBeforeLaunch()
    {
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            "DIM text=CHR(34)\nFOR i=1 TO 14\ntext=text+text\nNEXT\n"
            + "TRY\nPOWERSHELL(text)\nEXCEPT\nPRINT \"caught\"\nENDTRY", output.Add);
        Assert.Equal(["caught"], output);
    }

    [Fact]
    public async Task PublicFunctionRunsTheLastPayloadThatFitsTheCommandLine()
    {
        const int maximumLength = 32766;
        const string suffix = "\r\n'完了'";
        int overhead = WindowsPowerShellCommand.Build("").Length;
        int lastLength = (maximumLength - overhead) / 8 * 3;
        string command = "#" + new string('あ', lastLength - suffix.Length - 1)
            + suffix;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Value result = await InvokeAsync(command, token: timeout.Token);

        Assert.Equal(Value.String("完了\r\n"), result);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("parent-wait")]
    public async Task CancellationStopsNativeDescendant(string mode)
    {
        string marker = Marker();
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<Value> running = InvokeAsync("& " + Literal(fixture)
            + " --process-output-probe " + mode + " 0 37 " + Literal(marker), token: cancel.Token);
        try
        {
            int id = await WaitForPidAsync(marker);
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
    public async Task NoWaitReturnsWhileOwnedEngineIsAlive()
    {
        string marker = Marker();
        string release = marker + ".go";
        Process? child = null;
        try
        {
            string source = "[IO.File]::WriteAllText(" + Literal(marker + ".pending")
                + ",[string]$PID);[IO.File]::Move(" + Literal(marker + ".pending")
                + "," + Literal(marker) + ");$deadline=[Diagnostics.Stopwatch]::StartNew();"
                + "while(-not [IO.File]::Exists(" + Literal(release) + ")){"
                + "if($deadline.ElapsedMilliseconds -gt 30000){exit 99};Start-Sleep -Milliseconds 25}";
            Value result = await InvokeAsync(source, noWait: true);
            Assert.Equal("", result.Text);
            child = Process.GetProcessById(await WaitForPidAsync(marker));
            _ = child.SafeHandle;
            Assert.False(child.HasExited);
            await File.WriteAllTextAsync(release, "go");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
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

            File.Delete(marker);
            File.Delete(release);
        }
    }

    [Fact]
    public async Task CaptureWaitsForDescendantAfterPowerShellExit()
    {
        string marker = Marker();
        string rootMarker = marker + ".root";
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        string arguments = "--process-output-probe gated 0 37 \"" + marker + "\"";
        string source = "Start-Process -NoNewWindow -FilePath " + Literal(fixture)
            + " -ArgumentList " + Literal(arguments) + ";"
            + "[IO.File]::WriteAllText(" + Literal(rootMarker + ".pending")
            + ",[string]$PID);[IO.File]::Move(" + Literal(rootMarker + ".pending")
            + "," + Literal(rootMarker) + ");$deadline=[Diagnostics.Stopwatch]::StartNew();"
            + "while(-not [IO.File]::Exists(" + Literal(rootMarker + ".go") + ")){"
            + "if($deadline.ElapsedMilliseconds -gt 30000){exit 99};Start-Sleep -Milliseconds 25}";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<Value> running = InvokeAsync(source, token: cancel.Token);
        Process? root = null;
        Process? child = null;
        try
        {
            root = Process.GetProcessById(await WaitForPidAsync(rootMarker));
            _ = root.SafeHandle;
            child = Process.GetProcessById(await WaitForPidAsync(marker + ".started"));
            _ = child.SafeHandle;
            await File.WriteAllTextAsync(rootMarker + ".go", "go", cancel.Token);
            await root.WaitForExitAsync(cancel.Token);
            Assert.Equal(0, root.ExitCode);
            Assert.False(child.HasExited);
            Assert.False(running.IsCompleted);
            await File.WriteAllTextAsync(marker + ".go", "go", cancel.Token);
            Assert.Equal("late-output", (await running).Text);
            await child.WaitForExitAsync(cancel.Token);
            Assert.Equal(37, child.ExitCode);
        }
        finally
        {
            root?.Dispose();
            child?.Dispose();
            cancel.Cancel();
            await Record.ExceptionAsync(() => running);
            foreach (string path in new[] { marker, marker + ".started", marker + ".go",
                marker + ".write-error", rootMarker, rootMarker + ".go" })
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<Value> InvokeAsync(string command,
        WindowsCommandOptions? options = null, bool noWait = false,
        CancellationToken token = default)
    {
        await using IWindowsAutomationSession session = new WindowsAutomation
        {
            Commands = options ?? new WindowsCommandOptions()
        }.CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await session.InvokeAsync(BuiltinFunctionNames.PowerShell,
            [Value.String(command), Value.Numeric(noWait ? 1 : 0)],
            token.CanBeCanceled ? token : timeout.Token);
    }

    private static string Literal(string text) => "'"
        + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Marker() => Path.Combine(Path.GetTempPath(),
        "uwsc powershell marker " + Guid.NewGuid().ToString("N") + ".txt");

    private static async Task<int> WaitForPidAsync(string marker)
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
}
