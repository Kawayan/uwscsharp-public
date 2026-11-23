using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 所有する専用consoleでCLIの停止と終了処理を検証します。
/// </summary>
internal static class CliConsoleProbe
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);

    // NULL handlerによる無視属性は子へ継承されるため使いません。
    private static readonly ConsoleHandler Handler = _ => true;

    private sealed record Variant(string Name, bool Finally,
        string Cleanup, string Waiting);

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
            "uwsc-cli-console-" + Guid.NewGuid());
        try
        {
            // この子processだけを切り離し、新しい専用consoleへ接続します。
            if (GetConsoleProcessList(new uint[8], 8) != 0)
            {
                CheckNative(FreeConsole());
            }

            CheckNative(AllocConsole());
            ownsConsole = true;
            ShowWindow(GetConsoleWindow(), 0);
            CheckNative(SetConsoleCtrlHandler(Handler, true));
            handlerInstalled = true;
            Directory.CreateDirectory(directory);
            RunAsync(Path.GetFullPath(cli), mode, receipt, directory)
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                mode,
                error = ex.ToString()
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
        // check_preview_cancellation.pyと同じ取消・保存条件です。
        Variant[] variants =
        [
            new("default", false, "PRINT \"cleanup\"", "SLEEP(30)"),
            new("optfinally", true, "PRINT \"cleanup\"", "SLEEP(30)"),
            new("cleanup-error", true,
                "PRINT \"cleanup\"\nPRINT 1/0", "SLEEP(30)"),
            new("cleanup-timeout", true,
                "PRINT \"cleanup\"\nSLEEP(30)", "SLEEP(30)"),
            new("empty-while", true,
                "PRINT \"cleanup\"", "WHILE TRUE\nWEND"),
            new("empty-repeat", true,
                "PRINT \"cleanup\"", "REPEAT\nUNTIL FALSE"),
            new("empty-for", true,
                "PRINT \"cleanup\"", "FOR i=1 TO 1000000000\nNEXT"),
            new("cleanup-step-limit", true,
                "PRINT \"cleanup\"\nWHILE TRUE\nWEND", "SLEEP(30)"),
            new("save-failure", true,
                "PRINT \"cleanup\"", "SLEEP(30)"),
            new("sleep-maximum", true,
                "PRINT \"cleanup\"", "SLEEP(2147483.647)")
        ];
        var checks = new List<object>();
        foreach (Variant variant in variants)
        {
            string path = Path.Combine(directory, variant.Name + ".txt");
            string script = Path.Combine(directory, "cancel.uws");
            string source = (variant.Finally ? "OPTION OPTFINALLY\n" : "")
                + "DIM id=FOPEN(PARAM_STR[0],F_WRITE8)\n"
                + "FPUT(id,\"before\")\nTRY\n"
                + "PRINT \"READY 日本語\"\n" + variant.Waiting
                + "\nPRINT \"unreachable\"\nFINALLY\n"
                + variant.Cleanup + "\nENDTRY\n";
            File.WriteAllText(script, source, Utf8);
            using Process process = Start(cli, [script, path]);
            try
            {
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                string? ready = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(Deadline);
                Require(ready == "READY 日本語", "READY: " + ready);
                if (variant.Name == "save-failure")
                {
                    Directory.CreateDirectory(path);
                }

                VerifyOwnedConsole(process.Id);
                // group=0は専用console内だけへ送ります。
                // Ctrl+Cはprocess groupを指定して対象化できません。
                CheckNative(GenerateConsoleCtrlEvent(
                    mode == "ctrl-c" ? 0u : 1u, 0));
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(Deadline);
                string actualOutput = Normalize(await stdout.WaitAsync(Deadline));
                string actualError = await stderr.WaitAsync(Deadline);
                Require(process.ExitCode == 130,
                    variant.Name + ": " + process.ExitCode + actualError);
                Require(actualOutput == (variant.Finally ? "cleanup\n" : ""),
                    variant.Name + ": " + actualOutput);
                Require(actualError.Contains("実行を停止しました。",
                    StringComparison.Ordinal), actualError);
                if (variant.Name is "cleanup-error" or "cleanup-timeout"
                    or "cleanup-step-limit")
                {
                    Require(actualError.Contains("FINALLY:",
                        StringComparison.Ordinal), actualError);
                }

                if (variant.Name == "save-failure")
                {
                    Require(Directory.Exists(path), path);
                    Require(actualError.Contains("終了処理:",
                        StringComparison.Ordinal), actualError);
                    Directory.Delete(path);
                }
                else
                {
                    Require(File.ReadAllBytes(path).AsSpan()
                        .SequenceEqual("before\r\n"u8), path);
                    File.Delete(path);
                }

                await VerifyReopenAsync(cli, script, path);
                checks.Add(new
                {
                    name = variant.Name,
                    exit_code = process.ExitCode,
                    stdout = actualOutput,
                    stderr = actualError,
                    cleanup = variant.Finally,
                    rerun_and_reopen = true
                });
            }
            finally
            {
                await StopOwnedProcessAsync(process);
            }
        }

        await VerifyExitCodesAsync(cli, directory, checks);
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
    }

    private static async Task VerifyReopenAsync(string cli,
        string script, string path)
    {
        File.WriteAllText(script,
            "DIM id=FOPEN(PARAM_STR[0],F_WRITE8)\n"
            + "FPUT(id,\"rerun\")\nFCLOSE(id)\n", Utf8);
        (int code, string stdout, string stderr) =
            await ExecuteAsync(cli, [script, path]);
        Require(code == 0 && stdout.Length == 0 && stderr.Length == 0,
            $"再実行: {code}, {stdout}, {stderr}");
        Require(File.ReadAllBytes(path).AsSpan()
            .SequenceEqual("rerun\r\n"u8), path);
        File.Delete(path);
    }

    private static async Task VerifyExitCodesAsync(string cli,
        string directory, List<object> checks)
    {
        string script = Path.Combine(directory, "completion.uws");
        await VerifyAsync("usage", [], 2);
        await VerifyAsync("missing-source",
            [Path.Combine(directory, "missing.uws")], 1);
        await VerifyAsync("source-directory", [directory], 1);
        File.WriteAllText(script, "PRINT 1/0\n", Utf8);
        await VerifyAsync("runtime-error", [script], 1);
        File.WriteAllText(script, "EXITEXIT 37\n", Utf8);
        await VerifyAsync("explicit-exit", [script], 37);
        File.WriteAllText(script,
            "DIM id=FOPEN(PARAM_STR[0],F_WRITE8)\n"
            + "FPUT(id,\"blocked\")\nFCLOSE(id)\n", Utf8);
        await VerifyAsync("write-directory", [script, directory], 1);

        async Task VerifyAsync(string name, string[] args, int expected)
        {
            (int code, string stdout, string stderr) =
                await ExecuteAsync(cli, args);
            Require(code == expected, $"{name}: {code}, {stdout}, {stderr}");
            Require(expected == 37 ? stderr.Length == 0 : stderr.Length > 0,
                name + ": " + stderr);
            checks.Add(new
            {
                name,
                exit_code = code,
                stdout,
                stderr
            });
        }
    }

    private static async Task<(int, string, string)> ExecuteAsync(
        string cli, string[] args)
    {
        using Process process = Start(cli, args);
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(Deadline);
            return (process.ExitCode, await stdout.WaitAsync(Deadline),
                await stderr.WaitAsync(Deadline));
        }
        finally
        {
            await StopOwnedProcessAsync(process);
        }
    }

    private static Process Start(string cli, string[] args)
    {
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            // 専用consoleを継承し、テスト呼出元のconsoleは使いません。
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        foreach (string argument in args)
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

    private static void VerifyOwnedConsole(int childId)
    {
        uint[] ids = new uint[8];
        uint count = GetConsoleProcessList(ids, (uint)ids.Length);
        Require(count == 2
            && ids.Take(2).Contains((uint)Environment.ProcessId)
            && ids.Take(2).Contains((uint)childId),
            "専用consoleに所有対象外のprocessがいます。");
    }

    private static string Normalize(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal);

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
