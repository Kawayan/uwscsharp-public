using System.Diagnostics;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class UiaSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProviderCoversGridFailuresAndListItemShapes(bool knownFailure)
    {
        string fixture = FixturePaths.Get("UWSCSharp.UiaFixture",
            "WPFApplication.UWSCSharp.UiaFixture.dll");
        Assert.True(File.Exists(fixture), fixture);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(fixture);
        using Process process = Process.Start(start)!;
        try
        {
            string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(long.TryParse(ready, out long handle), ready);
            nint root = checked((nint)handle);
            Assert.Equal(" first ", await UiaSelection.ReadAsync(root, 1, 0, default));
            Assert.Equal(" first ", await UiaSelection.ReadAsync(root, 1, 1, default));
            foreach (int column in new[] { -1, -2, int.MinValue + 1 })
            {
                Assert.Equal(" first ", await UiaSelection.ReadAsync(root, 1, column, default));
            }

            await Assert.ThrowsAsync<OverflowException>(() =>
                UiaSelection.ReadAsync(root, 1, int.MinValue, default));
            Assert.Equal("", await UiaSelection.ReadAsync(root, 2, int.MinValue, default));
            Assert.Equal("日本語", await UiaSelection.ReadAsync(root, 1, 2, default));
            Assert.Equal("", await UiaSelection.ReadAsync(root, 1, 3, default));
            Assert.Equal("", await UiaSelection.ReadAsync(root, 2, 0, default));
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(_ =>
                throw new InvalidOperationException("物理入力は禁止です。"))).RunAsync(
                $"PRINT GETSLCTLST(HNDTOID({handle}),1,2)", output.Add);
            Assert.Equal(["日本語"], output);
            await process.StandardInput.WriteLineAsync("disable");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("日本語", await UiaSelection.ReadAsync(root, 1, 2, default));
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation(_ =>
                throw new InvalidOperationException("物理入力は禁止です。"))).RunAsync(
                $"PRINT GETSLCTLST(HNDTOID({handle}),1,2)", output.Add);
            Assert.Equal(["日本語"], output);
            output.Clear();
            await new Interpreter(windows: new WindowsAutomation(_ =>
                throw new InvalidOperationException("物理入力は禁止です。"))).RunAsync(
                $"PRINT GETSLCTLST(HNDTOID({handle}),1,-1)", output.Add);
            Assert.Equal([" first "], output);
            await process.StandardInput.WriteLineAsync("clear");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("", await UiaSelection.ReadAsync(root, 1, 2, default));
            await Assert.ThrowsAsync<OverflowException>(() =>
                UiaSelection.ReadAsync(root, 1, int.MinValue, default));
            await process.StandardInput.WriteLineAsync(knownFailure ? "fail-known" : "fail-unknown");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            // 取得結果とproviderメソッドへの到達を別々に確認します。
            Assert.Equal("", await UiaSelection.ReadAsync(root, 1, 2, default));
            await process.StandardInput.WriteLineAsync("stats");
            Assert.Equal("1", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync(knownFailure ? "list-nested" : "list-direct");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("日本語\t", await UiaSelection.ReadAsync(root, 1, 0, default));
            await process.StandardInput.WriteLineAsync("scrollstats");
            Assert.Equal("100,42", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync(knownFailure ? "combo-expanded" : "combo");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("日本語\t", await UiaSelection.ReadAsync(root, 1, 0, default));
            await process.StandardInput.WriteLineAsync("combostats");
            Assert.Equal("Expand,Collapse:Collapsed", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync(knownFailure
                ? "combo-selection-expanded" : "combo-selection");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("日本語\t", await UiaSelection.ReadAsync(root, 1, 0, default));
            await process.StandardInput.WriteLineAsync("combostats");
            string state = knownFailure ? ":Expanded" : ":Collapsed";
            Assert.Equal(state, await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync("clear-list");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("", await UiaSelection.ReadAsync(root, 1, 0, default));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    UiaSelection.ReadAsync(root, 1, 0, cancellation.Token));
            }

            await process.StandardInput.WriteLineAsync("combostats");
            Assert.Equal(state, await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await UiaSelection.ReadAsync(root, 1, 2, default));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
