using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowsPowerShellCommandTests
{
    [Fact]
    public void BuildAcceptsTheLastPayloadThatFitsTheWindowsCommandLine()
    {
        const int maximumLength = 32766;
        int overhead = WindowsPowerShellCommand.Build("").Length;
        // UTF-16 の3コード単位を符号化すると、Base64 の8文字になります。
        int lastLength = (maximumLength - overhead) / 8 * 3;
        string command = WindowsPowerShellCommand.Build(
            new string('あ', lastLength));
        Assert.InRange(command.Length, maximumLength - 7, maximumLength);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            WindowsPowerShellCommand.Build(new string('あ', lastLength + 1)));
        Assert.Contains("32766", error.Message);
    }

    [Fact]
    public async Task RuntimeParsingPreservesExplicitExitCode()
    {
        using CapturedProcess process = await CapturedProcess.StartAsync(
            WindowsPowerShellCommand.Executable, WindowsPowerShellCommand.Build("exit 37"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            Assert.Empty(await process.ReadOutputAsync(10000, timeout.Token));
            Assert.Equal(37u, await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            process.TerminateTree(1);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }

    [Theory]
    [InlineData("'日本語😀'", "日本語😀\r\n")]
    [InlineData("[Console]::Out.Write('A'+[char]0+'B')", "A\0B")]
    [InlineData("[Console]::OpenStandardInput().ReadByte()", "-1\r\n")]
    [InlineData("'quote: \" and backslash: \\'", "quote: \" and backslash: \\\r\n")]
    [InlineData("$a=1\r\n$a+1", "2\r\n")]
    [InlineData("[Console]::Out.Write('out|');[Console]::Error.Write('err|')", "out|err|")]
    [InlineData("", "")]
    [InlineData("Write-Output C:\\temp\\", "C:\\temp\\\r\n")]
    [InlineData("'a\\\"b'", "a\\\"b\r\n")]
    [InlineData("'a\\\\\"b'", "a\\\\\"b\r\n")]
    public async Task HiddenNativeLaunchUsesUtf8TextAndEof(string script, string expected)
    {
        using CapturedProcess process = await CapturedProcess.StartAsync(
            WindowsPowerShellCommand.Executable, WindowsPowerShellCommand.Build(script));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            byte[] bytes = await process.ReadOutputAsync(10000, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(expected, new UTF8Encoding(false, true).GetString(bytes));
        }
        finally
        {
            process.TerminateTree(1);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }

    [Theory]
    [InlineData("Write-Error 'record-error';'after-error'", "record-error")]
    [InlineData("throw 'terminating-error'", "terminating-error")]
    [InlineData("Read-Host 'prompt'", "NonInteractive")]
    public async Task PowerShellErrorsAreReadableText(string script, string expected)
    {
        using CapturedProcess process = await CapturedProcess.StartAsync(
            WindowsPowerShellCommand.Executable, WindowsPowerShellCommand.Build(script));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            string result = new UTF8Encoding(false, true).GetString(
                await process.ReadOutputAsync(10000, timeout.Token));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Contains(expected, result);
            Assert.DoesNotContain("CLIXML", result);
            Assert.DoesNotContain("<Objs", result);
        }
        finally
        {
            process.TerminateTree(1);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }
}
