using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 専用consoleでTHREADとCLI終了処理の統合を検証します。
/// </summary>
internal static partial class CliThreadConsoleProbe
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static readonly ConsoleHandler Handler = _ => true;

    public static int Run(string[] args)
    {
        if (args is not [string cli, string mode, string receipt]
            || mode is not ("ctrl-c" or "break"))
        {
            return 2;
        }

        bool ownsConsole = false;
        bool handlerInstalled = false;
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-thread-console-" + Guid.NewGuid());
        try
        {
            if (GetConsoleProcessList(new uint[8], 8) != 0)
            {
                CheckNative(FreeConsole());
            }

            CheckNative(AllocConsole());
            ownsConsole = true;
            ShowWindow(GetConsoleWindow(), 0);
            // 無視属性の継承を避け、fixture自身だけで信号を処理します。
            CheckNative(SetConsoleCtrlHandler(Handler, true));
            handlerInstalled = true;
            Directory.CreateDirectory(directory);
            RunAsync(Path.GetFullPath(cli), mode, receipt, directory)
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                mode,
                error = error.ToString()
            }), Utf8);
            return 1;
        }
        finally
        {
            if (handlerInstalled)
            {
                SetConsoleCtrlHandler(Handler, false);
            }

            if (ownsConsole)
            {
                FreeConsole();
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task RunAsync(string cli, string mode,
        string receipt, string directory)
    {
        var checks = new List<object>();
        await VerifyVariantAsync(false, false);
        await VerifyVariantAsync(true, false);
        await VerifyVariantAsync(true, true);
        File.WriteAllText(receipt, JsonSerializer.Serialize(new
        {
            result = "PASS",
            mode,
            console_scope = "owned-console-only",
            cli_sha256 = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(cli))),
            cli_assembly_sha256 = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.ChangeExtension(cli, ".dll")))),
            checks
        }, new JsonSerializerOptions { WriteIndented = true }), Utf8);

        async Task VerifyVariantAsync(bool finallyEnabled, bool workerExit)
        {
            string name = workerExit ? "worker-exit37"
                : finallyEnabled ? "thread-optfinally" : "thread-default";
            string mainFile = Path.Combine(directory, name + "-main.txt");
            string workerFile = Path.Combine(directory, name + "-worker.txt");
            string gate = Path.Combine(directory, name + "-gate.txt");
            string script = Path.Combine(directory, name + ".uws");
            File.WriteAllText(script,
                Source(finallyEnabled, workerExit), Utf8);
            using Process process = Start(cli,
                [script, mainFile, workerFile, gate]);
            try
            {
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                string[] ready = await ReadReadyAsync(process);
                VerifyConsole(process.Id);
                if (workerExit)
                {
                    // workerは両TRYが成立した後にだけEXITEXITへ進みます。
                    File.WriteAllText(gate, "release", Utf8);
                }
                else
                {
                    // group=0でも、専用consoleの所有する2processだけが対象です。
                    CheckNative(GenerateConsoleCtrlEvent(
                        mode == "ctrl-c" ? 0u : 1u, 0));
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(Deadline);
                string actualOutput = Normalize(await stdout.WaitAsync(Deadline));
                string actualError = await stderr.WaitAsync(Deadline);
                int expectedCode = workerExit ? 37 : 130;
                Require(process.ExitCode == expectedCode,
                    $"{name}: {process.ExitCode}, {actualError}");
                string[] cleanup = actualOutput.Split('\n',
                    StringSplitOptions.RemoveEmptyEntries);
                string[] expectedCleanup = finallyEnabled
                    ? ["MAIN_FINALLY", "WORKER_FINALLY"] : [];
                Require(cleanup.Order(StringComparer.Ordinal).SequenceEqual(
                    expectedCleanup.Order(StringComparer.Ordinal)),
                    name + ": " + actualOutput);
                Require(!actualOutput.Contains("UNREACHABLE",
                    StringComparison.Ordinal), actualOutput);
                Require(workerExit ? actualError.Length == 0
                    : actualError.Contains("実行を停止しました。",
                        StringComparison.Ordinal), actualError);
                VerifyFile(mainFile, "main", finallyEnabled);
                VerifyFile(workerFile, "worker", finallyEnabled);
                // WaitForExit後にもconsoleへの残留processが無いことを確認します。
                VerifyConsole(null);
                await VerifyRerunAsync(cli, script, mainFile, workerFile);
                VerifyConsole(null);
                checks.Add(new
                {
                    name,
                    exit_code = process.ExitCode,
                    ready,
                    stdout = actualOutput,
                    stderr = actualError,
                    cleanup = finallyEnabled,
                    signal_sent = !workerExit,
                    main_buffer_saved = true,
                    worker_buffer_saved = true,
                    natural_exit = true,
                    console_workers_remaining = 0,
                    rerun_and_reopen = true
                });
            }
            finally
            {
                // 期限超過時の回収であり、受入成功の終了条件には使いません。
                await StopOwnedProcessAsync(process);
            }
        }
    }

    private static string Source(bool finallyEnabled, bool workerExit)
    {
        string action = workerExit
            ? "WHILE FOPEN(gate,F_EXISTS)=FALSE\nSLEEP(0.002)\nWEND\nEXITEXIT 37"
            : "SLEEP(30)";
        return (finallyEnabled ? "OPTION OPTFINALLY\n" : "") + """
            DIM mainId=FOPEN(PARAM_STR[0],F_WRITE8)
            FPUT(mainId,"main-before")
            THREAD Worker(PARAM_STR[1],PARAM_STR[2])
            TRY
                PRINT "MAIN_READY"
                SLEEP(30)
                PRINT "MAIN_UNREACHABLE"
            FINALLY
                PRINT "MAIN_FINALLY"
                FPUT(mainId,"main-finally")
            ENDTRY
            PROCEDURE Worker(path,gate)
                DIM workerId=FOPEN(path,F_WRITE8)
                FPUT(workerId,"worker-before")
                TRY
                    PRINT "WORKER_READY"
            """ + "\n" + action + "\n" + """
                    PRINT "WORKER_UNREACHABLE"
                FINALLY
                    PRINT "WORKER_FINALLY"
                    FPUT(workerId,"worker-finally")
                ENDTRY
            FEND
            """;
    }

    private static async Task<string[]> ReadReadyAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        var ready = new HashSet<string>(StringComparer.Ordinal);
        while (ready.Count < 2)
        {
            string? line = await process.StandardOutput.ReadLineAsync(
                timeout.Token);
            Require(line is "MAIN_READY" or "WORKER_READY",
                "TRY開始marker: " + line);
            Require(ready.Add(line!), "TRY開始markerが重複しました。");
        }

        return [.. ready.Order(StringComparer.Ordinal)];
    }

    private static void VerifyFile(string path, string owner,
        bool finallyEnabled)
    {
        string expected = owner + "-before\r\n"
            + (finallyEnabled ? owner + "-finally\r\n" : "");
        Require(File.ReadAllBytes(path).AsSpan().SequenceEqual(
            Utf8.GetBytes(expected)), path);
    }

    private static async Task VerifyRerunAsync(string cli,
        string script, string mainFile, string workerFile)
    {
        File.WriteAllText(script, """
            PUBLIC ready=0
            PRINT ready
            THREAD Worker(PARAM_STR[1])
            WHILE ready=0
                SLEEP(0.002)
            WEND
            DIM id=FOPEN(PARAM_STR[0],F_WRITE8)
            FPUT(id,"rerun-main")
            FCLOSE(id)
            PRINT ready
            PROCEDURE Worker(path)
                DIM id=FOPEN(path,F_WRITE8)
                FPUT(id,"rerun-worker")
                FCLOSE(id)
                ready=1
            FEND
            """, Utf8);
        using Process process = Start(cli, [script, mainFile, workerFile]);
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(Deadline);
            string actualOutput = Normalize(await stdout.WaitAsync(Deadline));
            string actualError = await stderr.WaitAsync(Deadline);
            Require(process.ExitCode == 0 && actualOutput == "0\n1\n"
                && actualError.Length == 0,
                $"THREAD再実行: {process.ExitCode}, {actualOutput}, {actualError}");
            Require(File.ReadAllBytes(mainFile).AsSpan().SequenceEqual(
                "rerun-main\r\n"u8), mainFile);
            Require(File.ReadAllBytes(workerFile).AsSpan().SequenceEqual(
                "rerun-worker\r\n"u8), workerFile);
        }
        finally
        {
            await StopOwnedProcessAsync(process);
        }
    }

    private static Process Start(string cli, string[] arguments)
    {
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            Require(started, "CLIを起動できません。");
            process.StandardInput.Close();
            return process;
        }
        catch
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)Deadline.TotalMilliseconds);
            }

            process.Dispose();
            throw;
        }
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(Deadline);
        }
    }

    private static void VerifyConsole(int? childId)
    {
        uint[] ids = new uint[8];
        uint count = GetConsoleProcessList(ids, (uint)ids.Length);
        bool valid = count == (childId.HasValue ? 2u : 1u)
            && ids.Take((int)count).Contains((uint)Environment.ProcessId)
            && (!childId.HasValue
                || ids.Take((int)count).Contains((uint)childId.Value));
        Require(valid, "専用consoleの所有process構成が一致しません。");
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void CheckNative(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private delegate bool ConsoleHandler(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(
        [Out] uint[] processList, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent,
        uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleHandler handler, bool add);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
}
