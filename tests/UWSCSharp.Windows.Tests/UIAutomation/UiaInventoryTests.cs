using System.Diagnostics;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class UiaInventoryTests
{
    [Fact]
    public async Task GridInventoryPublishesColumnsHeadersAndIndependentSnapshots()
    {
        string fixture = FixturePaths.Get("UWSCSharp.UiaFixture",
            "WPFApplication.UWSCSharp.UiaFixture.dll");
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
            string? ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(long.TryParse(ready, out long handle), ready);
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"))
                .CreateSession();
            Value id = await session.InvokeAsync("HNDTOID",
                [Value.Numeric(handle)], default);
            IReadOnlyList<string>? previous = null;
            foreach (int column in new[] { 0, 1, 2, -1, 3 })
            {
                string[] expected = column switch
                {
                    0 or 1 => [" first ", "second"],
                    2 => ["日本語", ""],
                    -1 => ["First", "Second"],
                    _ => []
                };
                Assert.Equal(Value.Numeric(expected.Length),
                    await session.InvokeAsync("GETITEM",
                        [id, Value.Numeric(32), Value.Numeric(1), Value.Numeric(column)], default));
                Assert.Equal(expected, session.AllItems);
                if (previous is not null)
                {
                    Assert.Equal([" first ", "second"], previous);
                }

                previous ??= session.AllItems;
            }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            IReadOnlyList<string> snapshot = session.AllItems;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("GETITEM", [id, Value.Numeric(32)], cancellation.Token));
            Assert.Same(snapshot, session.AllItems);
            List<string> prefix = [];
            var failure = new InvalidOperationException("2項目目で失敗");
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                UiaItemInventory.ReadAsync(checked((nint)handle), ItemKinds.ListView,
                    1, 0, true, default, text =>
                    {
                        if (prefix.Count == 1)
                        {
                            throw failure;
                        }

                        prefix.Add(text);
                    })));
            Assert.Equal([" first "], prefix);
            await process.StandardInput.WriteLineAsync("headers-no-table");
            Assert.Equal("OK", await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(32), Value.Numeric(1), Value.Numeric(-1)], default));
            Assert.Equal(["First", "Second"], session.AllItems);
            Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(32), Value.Numeric(1), Value.Numeric(2)], default));
            Assert.Equal(["日本語", ""], session.AllItems);
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
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
