using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SandboxHostDeadlineTests
{
    [Fact]
    public async Task UnresponsiveGuestRequiresHostDeadline()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var request = JsonDocument.Parse(
            File.ReadAllText(@"C:\ui-input\test-request.json"));
        int timeout = request.RootElement.GetProperty("timeout_seconds").GetInt32();
        Assert.InRange(timeout, 1, 120);

        // 入力を禁止せず、ゲスト側で結果を返さない状態を作ります。
        string marker = @"C:\ui-output\host-deadline-ready.json";
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new
        {
            process_id = Environment.ProcessId,
            input_blocked = false,
            timeout_seconds = timeout,
        }));
        await Task.Delay(Timeout.Infinite);
    }
}
