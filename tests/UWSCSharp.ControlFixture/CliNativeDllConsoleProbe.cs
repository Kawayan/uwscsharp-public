using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace UWSCSharp.ControlFixture;

internal static partial class CliThreadConsoleProbe
{
    /// <summary>
    /// Sandboxの専用コンソールで、実DLL呼出し中のCLIへ取消信号を送ります。
    /// </summary>
    public static int RunNativeLibrary(string[] args)
    {
        if (args is not [string cli, string mode, string script,
            string requestName, string sentName, string receipt]
            || mode is not ("ctrl-c" or "break"))
        {
            return 2;
        }

        bool ownsConsole = false;
        bool handlerInstalled = false;
        try
        {
            Require(Environment.UserName == "WDAGUtilityAccount",
                "この検証はSandbox専用です。");
            if (GetConsoleProcessList(new uint[8], 8) != 0)
            {
                CheckNative(FreeConsole());
            }
            CheckNative(AllocConsole());
            ownsConsole = true;
            ShowWindow(GetConsoleWindow(), 0);
            CheckNative(SetConsoleCtrlHandler(Handler, true));
            handlerInstalled = true;
            VerifyNativeLibraryAsync(cli, mode, script,
                requestName, sentName, receipt).GetAwaiter().GetResult();
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
        }
    }

    private static async Task VerifyNativeLibraryAsync(string cli, string mode,
        string script, string requestName, string sentName, string receipt)
    {
        using var request = EventWaitHandle.OpenExisting(requestName);
        using var sent = EventWaitHandle.OpenExisting(sentName);
        using Process process = Start(cli, [script]);
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            File.WriteAllText(receipt + ".ready.tmp", process.Id.ToString(
                CultureInfo.InvariantCulture), Utf8);
            File.Move(receipt + ".ready.tmp", receipt + ".ready");
            Require(request.WaitOne(Deadline), "取消要求待ちが期限を超えました。");
            VerifyConsole(process.Id);
            CheckNative(GenerateConsoleCtrlEvent(mode == "ctrl-c" ? 0u : 1u, 0));
            sent.Set();
            await process.WaitForExitAsync().WaitAsync(Deadline);
            string actualOutput = Normalize(await stdout.WaitAsync(Deadline));
            string actualError = await stderr.WaitAsync(Deadline);
            Require(process.ExitCode == 130, "CLIの取消終了コードが一致しません。");
            Require(actualError.Contains("実行を停止しました。",
                StringComparison.Ordinal), actualError);
            VerifyConsole(null);
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "PASS",
                mode,
                child_pid = process.Id,
                code = process.ExitCode,
                stdout = actualOutput,
                stderr = actualError,
                console_scope = "owned-console-only",
                console_workers_remaining = 0,
                natural_exit = true,
                cli_sha256 = Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(cli))).ToLowerInvariant(),
                cli_assembly_sha256 = Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(Path.ChangeExtension(cli, ".dll"))))
                    .ToLowerInvariant()
            }), Utf8);
        }
        finally
        {
            await StopOwnedProcessAsync(process);
        }
    }
}
