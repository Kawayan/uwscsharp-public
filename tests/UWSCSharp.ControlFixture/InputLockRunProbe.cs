using System.ComponentModel;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

// プロセスを生存させたまま、終了した実行の入力禁止を次の実行が取得できるか調べます。
internal static class InputLockRunProbe
{
    internal static async Task RunAsync(string receipt, string ending)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("入力禁止の検証はSandbox専用です。");
        }
        await WaitAsync(receipt + ".go");
        bool firstDown = false;
        bool contenderRejected = false;
        bool firstUp = false;
        bool secondDown = false;
        bool secondContenderRejected = false;
        bool secondUp = false;
        string outcome = "none";
        string? failure = null;
        try
        {
            var interpreter = new Interpreter(windows: new WindowsAutomation());
            using var cancellation = new CancellationTokenSource();
            string tail = ending switch
            {
                "normal" => "",
                "exit" => "EXITEXIT(7)",
                "error" => "DIM failure=1/0",
                "cancel" => "SLEEP(60)",
                _ => throw new ArgumentException("未対応の終了条件です。", nameof(ending))
            };
            try
            {
                await interpreter.RunAsync("KBD(135,2)\nLOCKHARD(TRUE)\nKBD(135,1)\nSLEEP(0.05)\nPRINT 1\n" + tail,
                    _ =>
                    {
                        firstDown = IsDown();
                        contenderRejected = IsContenderRejected();
                        if (ending == "cancel")
                        {
                            cancellation.Cancel();
                        }
                    }, cancellation.Token);
                outcome = "completed";
            }
            catch (OperationCanceledException error) when (ending == "cancel"
                && error.CancellationToken == cancellation.Token)
            {
                outcome = "cancelled";
            }
            catch (ScriptException) when (ending == "error")
            {
                outcome = "script-error";
            }

            await Task.Delay(50);
            firstUp = !IsDown();
            await interpreter.RunAsync("LOCKHARD(TRUE)\nKBD(135,1)\nSLEEP(0.05)\nPRINT 1",
                _ =>
                {
                    secondDown = IsDown();
                    secondContenderRejected = IsContenderRejected();
                });
            await Task.Delay(50);
            secondUp = !IsDown();
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }

        await File.WriteAllTextAsync(receipt + ".pending", JsonSerializer.Serialize(new
        {
            process_id = Environment.ProcessId,
            ending,
            outcome,
            firstDown,
            contenderRejected,
            firstUp,
            secondDown,
            secondContenderRejected,
            secondUp,
            failure
        }));
        File.Move(receipt + ".pending", receipt);
        await WaitAsync(receipt + ".exit");
    }

    private static bool IsContenderRejected()
    {
        var contender = new NativeInputOwnershipBackend();
        try
        {
            contender.SetBlocked(true);
        }
        catch (Win32Exception)
        {
            return true;
        }
        contender.SetBlocked(false);
        return false;
    }

    private static bool IsDown() => (new NativeKeyStateBackend().ReadAsync(135) & 0x8000) != 0;

    private static async Task WaitAsync(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
