using System.Diagnostics;
using System.Text.Json;
using UWSCSharp.Windows;
using UWSCSharp.Wpf;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 窓を作らず、予約Jobの子孫と所有者の終了を検査します。
/// </summary>
internal static class SchedulerJobProbe
{
    internal static async Task<int> RunAsync(string[] options)
    {
        try
        {
            return await RunCoreAsync(options);
        }
        catch (Exception error)
        {
            if (options.Length >= 3)
            {
                int index = options[0].StartsWith("owner", StringComparison.Ordinal) ? 2 : 1;
                File.WriteAllText(options[index] + ".error", error.ToString());
            }
            throw;
        }
    }

    private static async Task<int> RunCoreAsync(string[] options)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        if (options is ["owner" or "owner-release", string leasePath, string receipt, string release])
        {
            using SchedulerMonitorLease lease = SchedulerMonitorLease.TryAcquire(leasePath)
                ?? throw new InvalidOperationException("検証用監視所有権を取得できません。");
            using var group = new SchedulerProcessGroup(lease.JobIdentity, lease.Handle);
            await group.RecoverAsync(timeout.Token);
            using SchedulerChildProcess worker = group.Start(Environment.ProcessPath!,
                ["--scheduler-job-probe", "spawn", receipt + ".worker", release],
                Environment.CurrentDirectory, timeout.Token);
            try
            {
                using JsonDocument workerReceipt = await ReadReceipt(receipt + ".worker", timeout.Token);
                if (options[0] == "owner-release")
                {
                    // 親の終了中に、所有ファイルがJobより先に閉じる窓を再現します。
                    lease.Dispose();
                }
                Publish(receipt, new
                {
                    pid = Environment.ProcessId,
                    worker_pid = worker.Id,
                    descendant_pid = workerReceipt.RootElement.GetProperty("child_pid").GetInt32(),
                    job_identity = lease.JobIdentity
                });
                await WaitForFile(release, timeout.Token);
            }
            finally
            {
                worker.Dispose();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await group.WaitForDrainAsync(cleanup.Token);
            }
            return 0;
        }

        if (options is ["spawn" or "orphan", string dataPath, string stopPath,
            .. string[] arguments])
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "--launcher-child-probe", dataPath + ".child", stopPath })
            {
                start.ArgumentList.Add(argument);
            }
            using Process child = Process.Start(start)
                ?? throw new InvalidOperationException("検証用子孫を開始できません。");
            using JsonDocument childReceipt = await ReadReceipt(dataPath + ".child", timeout.Token);
            Publish(dataPath, new
            {
                pid = Environment.ProcessId,
                child_pid = childReceipt.RootElement.GetProperty("pid").GetInt32(),
                working_directory = Environment.CurrentDirectory,
                arguments
            });
            if (options[0] == "spawn")
            {
                await WaitForFile(stopPath, timeout.Token);
                await child.WaitForExitAsync(timeout.Token);
            }
            return 0;
        }
        throw new ArgumentException("予約Job検証の起動引数が不正です。", nameof(options));
    }

    private static async Task WaitForFile(string path, CancellationToken token)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(25, token);
        }
    }

    private static async Task<JsonDocument> ReadReceipt(string path, CancellationToken token)
    {
        await WaitForFile(path, token);
        return await ProcessReceiptReader.ReadJsonAsync(path, token);
    }

    private static void Publish(string path, object value)
    {
        File.WriteAllText(path + ".prepared", JsonSerializer.Serialize(value));
        File.Move(path + ".prepared", path);
    }
}
