using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

internal static class InputLockTouchProbe
{
    internal static async Task RunAsync(string receipt, bool blocked, string ending, int contactCount)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("入力禁止の検証はSandbox専用です。");
        }
        Gate(receipt + ".ready", receipt + ".go");
        string[] position = (await File.ReadAllTextAsync(receipt + ".target")).Split(',');
        int x = int.Parse(position[0], CultureInfo.InvariantCulture);
        int y = int.Parse(position[1], CultureInfo.InvariantCulture);
        bool recovered = false;
        bool cancelObserved = false;
        int? exitCode = null;
        string outcome = "none";
        string? failure = null;
        var cleanupErrors = new List<string>();
        try
        {
            var interpreter = new Interpreter(windows: new WindowsAutomation());
            using var stop = new CancellationTokenSource();
            string down = string.Join("\n", Enumerable.Range(0, contactCount)
                .Select(index => $"BTN(7,1,{x + index * 40},{y})"));
            string up = string.Join("\n", Enumerable.Range(0, contactCount)
                .Select(index =>
                {
                    int dx = ending == "move-cancel" ? 100 - index * 20
                        : ending == "move" ? 12 - index * 4 : 0;
                    int dy = ending == "move-cancel" ? 60 + index * 20
                        : ending == "move" ? 8 + index * 4 : 0;
                    return $"BTN(7,2,{x + index * 40 + dx},{y + dy})";
                }));
            string tail = ending switch
            {
                "explicit" or "move" => up + "\nPRINT 2",
                "move-cancel" => up,
                "reuse" => up + "\nPRINT 2\n" + down + "\nPRINT 3\n" + up + "\nPRINT 4",
                "exit" => "EXITEXIT(7)",
                "normal" or "thread" or "run-reuse" or "thread-reuse"
                    or "thread-error" or "thread-cancel" => "",
                "error" => "DIM failure=1/0",
                "cancel" => "SLEEP(60)",
                _ => throw new ArgumentException("未対応の終了条件です。", nameof(ending))
            };
            string script = $"LOCKHARD({(blocked ? "TRUE" : "FALSE")})\n" + down + "\nPRINT 1\n" + tail;
            if (ending is "thread" or "thread-reuse" or "thread-error" or "thread-cancel")
            {
                string continuation = ending == "thread-reuse"
                    ? down + "\nPRINT 3\n" + up + "\nPRINT 4\n"
                    : "";
                string workerEnd = ending switch
                {
                    "thread-error" => "DIM failure=1/0",
                    "thread-cancel" => "SLEEP(60)",
                    _ => "done=1"
                };
                script = "PUBLIC done=0\nTHREAD Worker()\n"
                    + "WHILE done=0\nSLEEP(0.001)\nWEND\nPRINT 2\n"
                    + continuation
                    + "PROCEDURE Worker()\n"
                    + $"LOCKHARD({(blocked ? "TRUE" : "FALSE")})\n"
                    + down + "\nPRINT 1\n" + workerEnd + "\nFEND";
            }
            using var monitorStop = new CancellationTokenSource();
            Task<bool> cancellationMonitor = ending == "move-cancel"
                ? ObserveCancellationAsync(receipt + ".cancel", stop, monitorStop.Token)
                : Task.FromResult(false);
            try
            {
                var host = new CallbackScriptHost(value =>
                {
                    string stage = value switch
                    {
                        "2" => ".up",
                        "3" => ".held-reuse",
                        "4" => ".up-reuse",
                        _ => ".held"
                    };
                    Gate(receipt + stage, receipt + stage + ".ack");
                    if (value == "1" && ending is "cancel" or "thread-cancel")
                    {
                        stop.Cancel();
                    }
                });
                exitCode = await interpreter.RunWithExitCodeAsync(
                    script, host, stop.Token, ScriptRunOptions.Empty);
                if (ending == "run-reuse")
                {
                    // 最初の実行の後始末が完了してから次の実行を開始します。
                    Gate(receipt + ".up", receipt + ".up.ack");
                    string next = $"LOCKHARD({(blocked ? "TRUE" : "FALSE")})\n"
                        + down + "\nPRINT 3\n" + up + "\nPRINT 4";
                    exitCode = await interpreter.RunWithExitCodeAsync(
                        next, host, stop.Token, ScriptRunOptions.Empty);
                }
                outcome = "completed";
            }
            catch (OperationCanceledException error) when ((ending is "cancel" or "move-cancel" or "thread-cancel") && error.CancellationToken == stop.Token)
            {
                outcome = "cancelled";
                cleanupErrors.AddRange(CleanupDiagnostics(error));
            }
            catch (ScriptException error) when (ending is "error" or "thread-error")
            {
                outcome = "script-error";
                cleanupErrors.AddRange(CleanupDiagnostics(error));
            }
            finally
            {
                monitorStop.Cancel();
                cancelObserved = await cancellationMonitor;
            }
            var contender = new NativeInputOwnershipBackend();
            contender.SetBlocked(true);
            contender.SetBlocked(false);
            recovered = true;
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        await File.WriteAllTextAsync(receipt + ".pending", JsonSerializer.Serialize(new
        {
            process_id = Environment.ProcessId,
            blocked,
            ending,
            contactCount,
            runCount = ending == "run-reuse" ? 2 : 1,
            digitizerFlags = Native.GetSystemMetrics(94),
            maximumTouches = Native.GetSystemMetrics(95),
            exitCode,
            outcome,
            recovered,
            cancelObserved,
            cleanupErrors,
            failure
        }));
        File.Move(receipt + ".pending", receipt);
        Gate(receipt + ".finished", receipt + ".exit");
    }

    private static IEnumerable<string> CleanupDiagnostics(Exception error)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(error);
        while (pending.TryPop(out Exception? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }
            foreach (System.Collections.DictionaryEntry entry in current.Data)
            {
                if (entry.Key is string key
                    && key.StartsWith("UWSCSharp.", StringComparison.Ordinal)
                    && key.EndsWith("Errors", StringComparison.Ordinal)
                    && entry.Value is not null)
                {
                    yield return key + ": " + entry.Value;
                    if (entry.Value is Exception diagnostic)
                    {
                        pending.Push(diagnostic);
                    }
                }
            }
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is Exception inner)
            {
                pending.Push(inner);
            }
        }
    }

    private static async Task<bool> ObserveCancellationAsync(string path,
        CancellationTokenSource executionStop, CancellationToken monitorStop)
    {
        try
        {
            while (!File.Exists(path))
            {
                await Task.Delay(5, monitorStop);
            }
            monitorStop.ThrowIfCancellationRequested();
            executionStop.Cancel();
            return true;
        }
        catch (OperationCanceledException) when (monitorStop.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void Gate(string marker, string acknowledgement)
    {
        File.WriteAllText(marker, "ready");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(acknowledgement))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("タッチ受信の確認が届きません。");
            }
            Thread.Sleep(10);
        }
    }
}
