using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using UWSCSharp.Wpf;

internal static class SchedulerIdentityProbe
{
    internal static async Task<int> RunAsync(string[] options, bool peer)
    {
        if (options is not [string control, string generation, string occurrence,
            string parent, string logs, string keys])
        {
            throw new ArgumentException("予約接続元検証の引数が不正です。", nameof(options));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string mode = File.ReadAllText(Path.Combine(logs, "identity-probe"));
        var identity = new SchedulerWorkerIdentity(Guid.ParseExact(generation, "N"), Guid.ParseExact(occurrence, "N"));
        Publish(Path.Combine(logs, peer ? "peer.json" : "child.json"), new
        {
            pid = Environment.ProcessId
        });
        Process? spawned = null;
        try
        {
            if (!peer && mode != "wrong-ready")
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                start.ArgumentList.Add("--scheduler-identity-peer");
                foreach (string argument in options)
                {
                    start.ArgumentList.Add(argument);
                }
                spawned = Process.Start(start) ?? throw new InvalidOperationException("別接続元を開始できません。");
            }
            while (!File.Exists(Path.Combine(logs, "connect")))
            {
                await Task.Delay(25, timeout.Token);
            }
            if (!peer && mode == "foreign-control")
            {
                await Task.Delay(Timeout.Infinite, timeout.Token);
                return 0;
            }
            using NamedPipeClientStream stream = await SchedulerWorkerPipe.ConnectAsync(
                peer && mode == "foreign-keyboard" ? keys : control, timeout.Token);
            if (peer && mode == "foreign-keyboard")
            {
                await Task.Delay(Timeout.Infinite, timeout.Token);
                return 0;
            }
            SchedulerWorkerMessage first = await SchedulerWorkerProtocol.ReadAsync(stream, identity, timeout.Token);
            if (first is not SchedulerWorkerMessage.Request)
            {
                throw new InvalidDataException("要求以外を受信しました。");
            }
            File.WriteAllText(Path.Combine(logs, "request-received"), "request");
            int claimed = mode == "wrong-ready" ? int.Parse(parent, CultureInfo.InvariantCulture) : Environment.ProcessId;
            if (peer)
            {
                using JsonDocument root = await Read(Path.Combine(logs, "child.json"));
                claimed = root.RootElement.GetProperty("pid").GetInt32();
            }
            Publish(Path.Combine(logs, "ready-announced.json"), new
            {
                claimed_pid = claimed
            });
            await SchedulerWorkerProtocol.WriteAsync(stream,
                new SchedulerWorkerMessage.Ready(identity, claimed), timeout.Token);
            while (true)
            {
                SchedulerWorkerMessage command = await SchedulerWorkerProtocol.ReadAsync(stream, identity, timeout.Token);
                if (command is SchedulerWorkerMessage.Start)
                {
                    File.WriteAllText(Path.Combine(logs, "start.bad"), "start");
                }
            }
        }
        finally
        {
            spawned?.Dispose();
        }
    }

    private static void Publish(string path, object value)
    {
        File.WriteAllText(path + ".prepared", JsonSerializer.Serialize(value));
        File.Move(path + ".prepared", path, overwrite: true);
    }

    private static async Task<JsonDocument> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonDocument.ParseAsync(stream);
    }
}
