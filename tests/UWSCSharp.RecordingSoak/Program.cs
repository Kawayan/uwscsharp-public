using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.RecordingSoak;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out int seconds) || seconds < 1
            || args[0] is not ("below-limit" or "saturated"))
        {
            Console.Error.WriteLine("使用法: RecordingSoak <below-limit|saturated> <秒数> <結果JSON>");
            return 2;
        }
        string output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            return await RunAsync(args[0], seconds, output);
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                Result = "FAIL",
                Error = error.ToString(),
                Mode = args[0],
                Seconds = seconds
            }, JsonOptions));
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string mode, int seconds, string output)
    {
        using var backend = new SoakBackend();
        using var recorder = new BasicRecorder(backend, backend);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var process = Process.GetCurrentProcess();
        var samples = new List<Sample>();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        await recorder.StartAsync();
        await backend.BatchAsync(0);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baselineHeap = GC.GetTotalMemory(true);
        long stableHeap = baselineHeap;
        bool stableMeasured = false;
        var elapsed = Stopwatch.StartNew();
        long sent = 0;
        long retained = 0;
        long cycles = 0;
        long maxBatchMs = 0;
        long maxGapMs = 0;
        long previousBatch = 0;
        double nextSample = 0;
        int batchCycles = mode == "saturated" ? 32 : 1;
        int intervalMs = mode == "saturated" ? 50 : 2000;
        Sample before = Measure(process, backend, elapsed.Elapsed.TotalSeconds);
        while (elapsed.Elapsed.TotalSeconds < seconds)
        {
            long batchStarted = elapsed.ElapsedMilliseconds;
            maxGapMs = Math.Max(maxGapMs, batchStarted - previousBatch);
            var latency = Stopwatch.StartNew();
            await backend.BatchAsync(batchCycles);
            maxBatchMs = Math.Max(maxBatchMs, latency.ElapsedMilliseconds);
            for (int index = 0; index < batchCycles; index++)
            {
                AppendExpected(expected, cycles++, ref retained);
                sent += 8;
            }
            previousBatch = batchStarted;
            if (!recorder.IsRunning || recorder.RecoveryCount != 0)
            {
                throw new InvalidOperationException("記録スレッドの終了または想定外のフック復旧を検出しました。");
            }
            if (!stableMeasured && elapsed.Elapsed.TotalSeconds >= Math.Min(30, seconds / 2d))
            {
                stableHeap = GC.GetTotalMemory(true);
                stableMeasured = true;
            }
            if (elapsed.Elapsed.TotalSeconds >= nextSample)
            {
                Sample sample = Measure(process, backend, elapsed.Elapsed.TotalSeconds);
                samples.Add(sample);
                await File.AppendAllTextAsync(output + ".samples.jsonl",
                    JsonSerializer.Serialize(sample) + Environment.NewLine);
                Console.WriteLine($"{mode}: seconds={sample.Seconds:F1}, callbacks={sample.Callbacks}, heap={sample.HeapBytes}, private={sample.PrivateBytes}");
                nextSample += 60;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(intervalMs,
                Math.Max(1, (seconds - elapsed.Elapsed.TotalSeconds) * 1000))));
        }
        Sample last = Measure(process, backend, elapsed.Elapsed.TotalSeconds);
        samples.Add(last);
        long finalRetainedHeap = GC.GetTotalMemory(true);
        var stopping = Stopwatch.StartNew();
        string source = recorder.Stop();
        stopping.Stop();
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long lines = 0;
        using (var reader = new StringReader(source))
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith(BuiltinFunctionNames.Sleep + "(", StringComparison.Ordinal))
                {
                    continue;
                }
                actual.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                lines++;
            }
        }
        string expectedHash = Convert.ToHexString(expected.GetHashAndReset());
        string actualHash = Convert.ToHexString(actual.GetHashAndReset());
        // 終了したランタイムスレッドの回収待ちを、ハンドルのリークと区別します。
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Sample afterCollection = Measure(process, backend, elapsed.Elapsed.TotalSeconds);
        bool passed = sent == backend.CallbackCount && lines == retained
            && actualHash == expectedHash && recorder.Overflowed == (sent > 50_000)
            && !recorder.IsRunning && backend.Released == 2
            && finalRetainedHeap - stableHeap < 16 * 1024 * 1024
            && maxGapMs < Math.Max(30_000, intervalMs * 3);
        var result = new
        {
            Result = passed ? "PASS" : "FAIL",
            Mode = mode,
            StartedUtc = started,
            EndedUtc = DateTimeOffset.UtcNow,
            RequestedSeconds = seconds,
            ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
            SentEvents = sent,
            CallbackEvents = backend.CallbackCount,
            RetainedEvents = lines,
            ExpectedRetainedEvents = retained,
            ExpectedOverflowDrops = Math.Max(0, sent - retained),
            recorder.Overflowed,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            BaselineRetainedHeap = baselineHeap,
            StableRetainedHeap = stableHeap,
            FinalRetainedHeap = finalRetainedHeap,
            RetainedHeapGrowth = finalRetainedHeap - stableHeap,
            PeakWorkingSet = process.PeakWorkingSet64,
            CallbackAllocatedBytes = backend.CallbackAllocatedBytes,
            MaximumBatchMilliseconds = maxBatchMs,
            MaximumBatchGapMilliseconds = maxGapMs,
            StopMilliseconds = stopping.ElapsedMilliseconds,
            WorkerStopped = !recorder.IsRunning,
            ReleasedHooks = backend.Released,
            OS = Environment.OSVersion.ToString(),
            Runtime = Environment.Version.ToString(),
            Before = before,
            AfterCollection = afterCollection,
            Samples = samples,
            Scope = "synthetic callbacks on recorder worker; no SendInput, no GUI automation, no physical-device acceptance"
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, JsonOptions));
        Console.WriteLine($"{result.Result}: {output}");
        return passed ? 0 : 1;
    }

    private static void AppendExpected(IncrementalHash expected, long cycle, ref long retained)
    {
        if (retained >= 50_000)
        {
            return;
        }
        int key = 65 + (int)(cycle % 26);
        int x = (int)(cycle % 500);
        int y = (int)(cycle % 300);
        string[] lines =
        [
            "KBD(160,1)", $"KBD({key},1)", $"MMV({x},{y})",
            $"BTN(0,1,{x},{y})", $"MMV({x + 1},{y + 1})",
            $"BTN(0,2,{x + 1},{y + 1})", $"KBD({key},2)", "KBD(160,2)"
        ];
        foreach (string line in lines)
        {
            if (retained++ < 50_000)
            {
                expected.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
            }
        }
    }

    private static Sample Measure(Process process, SoakBackend backend, double seconds)
    {
        process.Refresh();
        return new Sample(seconds, backend.CallbackCount, GC.GetTotalMemory(false),
            process.PrivateMemorySize64, process.WorkingSet64,
            GC.GetTotalAllocatedBytes(), backend.CallbackAllocatedBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            process.HandleCount, process.Threads.Count);
    }

    private sealed record Sample(double Seconds, long Callbacks, long HeapBytes,
        long PrivateBytes, long WorkingSetBytes, long AllocatedBytes, long CallbackAllocatedBytes,
        int Gen0, int Gen1, int Gen2, int Handles, int Threads);
}
