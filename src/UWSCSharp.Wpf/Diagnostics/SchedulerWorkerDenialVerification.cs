using System.IO;
using System.Text.Json;

namespace UWSCSharp.Wpf;

internal static class SchedulerWorkerDenialVerification
{
    internal static void SaveSettings(string settings, string script,
        string root, string mode)
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), mode, script, [],
            SchedulerExecutionMode.SeparateProcess,
            new SchedulerOneShotCondition(
                new DateOnly(2027, 1, 1), new TimeOnly(0, 1)), root);
        SchedulerSettingsStore.Save(settings,
            new SchedulerSettings([entry], enabled: true), null);
    }

    internal static MainWindow CreateWindow(string root,
        List<Exception> errors)
        => new(false, confirmDiscard: () => true, reportError: errors.Add,
            launcherSettingsPath: Path.Combine(root, "launcher.json"))
        {
            ShowActivated = false
        };

    internal static async Task<SchedulerRuntimeHost> InitializeWorkerAsync(
        MainWindow.SchedulerVerificationAccess access, string settings,
        string root, string logs, DenialClock clock)
    {
        access.Window.Show();
        await access.Window.RefreshInputCleanupAsync();
        await access.InitializeWorkerAsync(settings,
            Path.Combine(root, "monitor"), logs, clock);
        return access.Host!;
    }

    internal static async Task<SchedulerRuntimeHost.Execution> WaitForWorkerAsync(
        SchedulerRuntimeHost host, DenialClock clock, string root)
    {
        clock.Now = clock.Now.AddMinutes(1);
        host.Poll();
        await SchedulerVerification.WaitForRuntimeHost(() =>
            File.Exists(Path.Combine(root, "ready")));
        return host.Executions.Single();
    }

    internal static int ProbeReadOnlyReplacement(string path)
    {
        File.SetAttributes(path,
            File.GetAttributes(path) | FileAttributes.ReadOnly);
        string probe = Path.Combine(Path.GetDirectoryName(path)!,
            "access-probe.tmp");
        File.WriteAllText(probe, "probe");
        int denied = 0;
        try
        {
            File.Move(probe, path, overwrite: true);
        }
        catch (UnauthorizedAccessException error)
        {
            denied = error.HResult & 0xffff;
        }
        finally
        {
            File.Delete(probe);
        }

        return denied;
    }

    internal static async Task<byte[]> ReadSharedBytesAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    internal static async Task RestoreAsync(string? path, MainWindow main,
        MainWindow.SchedulerVerificationAccess access,
        SchedulerRuntimeHost? host)
    {
        if (path is not null && File.Exists(path))
        {
            File.SetAttributes(path,
                File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        if (host is not null && !access.IsClosed && !access.IsClosing)
        {
            await host.StopAsync(TimeSpan.FromSeconds(20));
            main.Close();
        }
    }

    internal static async Task<int> WriteReceiptAsync(string receipt,
        List<string> checks)
    {
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = "Passed",
            process_id = Environment.ProcessId,
            checks
        }));
        return 0;
    }

    internal sealed class DenialClock : TimeProvider
    {
        internal DateTimeOffset Now
        {
            get;
            set;
        } = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// 診断で指定したUTC時刻を返します。
        /// </summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
