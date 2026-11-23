using System.Globalization;
using System.Text.Json;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

// 親を終了させたときのJobの動作を、入力を禁止せず検証します。
internal static class WatchdogOwnerProbe
{
    internal static async Task RunAsync(string receipt, bool killOnClose)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("所有者終了の検証はSandbox専用です。");
        }

        string executable = Environment.ProcessPath!;
        string marker = receipt + ".child";
        using CapturedProcess target = await CapturedProcess.StartAsync(executable,
            $"\"{executable}\" --process-output-probe parent-wait 0 37 \"{marker}\"",
            killOnClose: killOnClose);
        Task<byte[]> output = target.ReadOutputAsync(64 * 1024);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker))
        {
            await Task.Delay(10, timeout.Token);
        }

        int child = int.Parse(await File.ReadAllTextAsync(marker, timeout.Token), CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(receipt + ".pending",
            JsonSerializer.Serialize(new
            {
                target = target.Id,
                child
            }));
        File.Move(receipt + ".pending", receipt);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        GC.KeepAlive(output);
        GC.KeepAlive(target);
    }
}
