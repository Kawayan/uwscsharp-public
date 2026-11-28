using UWSCSharp.Wpf;

namespace UWSCSharp.ControlFixture;

internal static class SchedulerStorageProbe
{
    internal static void CheckMonitorLease(string path)
    {
        using (SchedulerMonitorLease? first = SchedulerMonitorLease.TryAcquire(path))
        {
            if (first is not null)
            {
                throw new InvalidOperationException("親プロセスが所有する監視を取得しました。");
            }
        }

        Console.WriteLine("busy");
        Console.Out.Flush();
        if (Console.ReadLine() != "acquire")
        {
            throw new InvalidOperationException("親プロセスから取得要求がありません。");
        }

        using SchedulerMonitorLease lease = SchedulerMonitorLease.TryAcquire(path)
            ?? throw new InvalidOperationException("監視を取得できません。");
        Console.WriteLine(lease.Generation);
        Console.Out.Flush();
        Console.ReadLine();
    }

    internal static void ResaveSettings(string path)
    {
        SchedulerSettingsSnapshot snapshot = SchedulerSettingsStore.Load(path);
        SchedulerSettingsSnapshot saved = SchedulerSettingsStore.Save(path, snapshot.Settings, snapshot.Revision);
        Console.WriteLine(saved.Revision);
    }
}
