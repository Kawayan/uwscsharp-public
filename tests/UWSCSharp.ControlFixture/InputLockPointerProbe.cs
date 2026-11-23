using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

// 入力禁止中の座標指定を実OSで確認します。
internal static class InputLockPointerProbe
{
    internal static async Task RunAsync(string receipt, string mode, int dpiContext)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("入力禁止の検証はSandbox専用です。");
        }
        if (!SetProcessDpiAwarenessContext(dpiContext))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        uint dpi;
        using (var scope = new PhysicalDpiScope())
        {
            dpi = GetDpiForSystem();
        }
        await WaitAsync(receipt + ".go");
        bool getStr = mode.StartsWith("getstr-", StringComparison.Ordinal);
        using JsonDocument? target = getStr
            ? JsonDocument.Parse(await File.ReadAllTextAsync(receipt + ".target")) : null;
        bool textMatched = !getStr;
        int actualX = 0;
        int actualY = 0;
        bool initial = false;
        bool initiallyBlocked = false;
        bool moved = false;
        bool blocked = false;
        bool recovered = false;
        string? failure = null;
        Native.Point original = ReadPosition();
        try
        {
            int kind = mode switch
            {
                "getstr-static" or "getstr-uia-static" => 1,
                "getstr-status" or "getstr-uia-status" => 2,
                "getstr-acc-edit" => 3,
                "getstr-acc-static" => 4,
                "getstr-acc-cell" => 5,
                _ => 0
            };
            string operation = mode switch
            {
                "mmv" => "MMV(200,180)",
                "button" => "BTN(2,2,200,180)",
                _ when getStr => "PRINT GETSTR(HNDTOID("
                    + target!.RootElement.GetProperty("window").GetInt64()
                    + "),1," + kind + ",TRUE)",
                _ => throw new ArgumentException("未対応の移動条件です。", nameof(mode))
            };
            var interpreter = new Interpreter(windows: new WindowsAutomation());
            await interpreter.RunAsync("MMV(100,100)\nLOCKHARD(TRUE)\nPRINT 0\n" + operation
                + "\nSLEEP(0.05)\nPRINT 1",
                value =>
                {
                    Native.Point position = ReadPosition();
                    if (value == "0")
                    {
                        initial = position.X == 100 && position.Y == 100;
                        initiallyBlocked = IsContenderRejected();
                        return;
                    }
                    if (getStr && value != "1")
                    {
                        textMatched = value == "入力禁止中の本文";
                        return;
                    }
                    actualX = position.X;
                    actualY = position.Y;
                    moved = target is null
                        ? position.X == 200 && position.Y == 180
                        : position.X == target.RootElement.GetProperty("left").GetInt32() + 4
                            && position.Y == target.RootElement.GetProperty("top").GetInt32() + 4;
                    blocked = IsContenderRejected();
                });
            var next = new NativeInputOwnershipBackend();
            next.SetBlocked(true);
            next.SetBlocked(false);
            recovered = true;
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        finally
        {
            using var restoreDpi = new PhysicalDpiScope();
            if (!Native.SetPhysicalCursorPos(original.X, original.Y))
            {
                failure = (failure ?? "") + " カーソル位置を復元できませんでした。";
            }
        }
        await File.WriteAllTextAsync(receipt + ".pending", JsonSerializer.Serialize(new
        {
            process_id = Environment.ProcessId,
            mode,
            dpiContext,
            dpi,
            actualX,
            actualY,
            target = target?.RootElement,
            textMatched,
            initial,
            initiallyBlocked,
            moved,
            blocked,
            recovered,
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

    private static Native.Point ReadPosition()
    {
        using var dpi = new PhysicalDpiScope();
        if (!GetPhysicalCursorPos(out Native.Point position))
        {
            throw new InvalidOperationException("カーソル位置を取得できませんでした。");
        }
        return position;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out Native.Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static async Task WaitAsync(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
