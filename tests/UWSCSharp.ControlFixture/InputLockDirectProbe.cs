using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

internal static class InputLockDirectProbe
{
    internal static async Task RunAsync(string receipt, string ending, int dpiContext)
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
        string window = await File.ReadAllTextAsync(receipt + ".target");
        bool blocked = false;
        bool pointerUnchanged = false;
        bool focusUnchanged = false;
        bool physicalKeyUp = false;
        bool recovered = false;
        string outcome = "none";
        string? failure = null;
        try
        {
            var interpreter = new Interpreter(windows: new WindowsAutomation());
            using var stop = new CancellationTokenSource();
            if ((new NativeKeyStateBackend().ReadAsync(135) & 0x8000) != 0)
            {
                throw new InvalidOperationException("検証開始時にF24が押されています。");
            }
            if (ending.StartsWith("omitted", StringComparison.Ordinal))
            {
                var target = new Native.Point(0, 0);
                using var scope = new PhysicalDpiScope();
                if (!Native.ClientToScreen(nint.Parse(window), ref target)
                    || !Native.SetPhysicalCursorPos(checked(target.X + 40), checked(target.Y + 50)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            Native.Point cursor = ReadCursor();
            nint foreground = Native.GetForegroundWindow();
            string tail = ending switch
            {
                "explicit" => "KBD(135,2)\nBTN(2,2,40,50)\nPRINT 2\nLOCKHARD(FALSE)\nPRINT 3",
                "normal" or "omitted" or "child" or "omitted-child" => "",
                "cancel" => "SLEEP(60)",
                "error" => "DIM failure=1/0",
                _ => throw new ArgumentException("未対応の終了条件です。", nameof(ending))
            };
            string down = ending.StartsWith("omitted", StringComparison.Ordinal) ? "BTN(2,1)" : "BTN(2,1,40,50)";
            string script = "LOCKHARD(TRUE)\nMOUSEORG(HNDTOID(" + window
                + "),2)\nMMV(40,50)\n" + down + "\nKBD(135,1)\nPRINT 1\n" + tail;
            try
            {
                await interpreter.RunAsync(script, value =>
                {
                    if (value is "2" or "3")
                    {
                        string stage = value == "2" ? ".up" : ".released";
                        Gate(receipt + stage, receipt + stage + ".ack");
                        return;
                    }
                    var contender = new NativeInputOwnershipBackend();
                    try
                    {
                        contender.SetBlocked(true);
                    }
                    catch (Win32Exception)
                    {
                        blocked = true;
                    }
                    if (!blocked)
                    {
                        contender.SetBlocked(false);
                    }
                    Gate(receipt + ".held", receipt + ".ack");
                    Native.Point current = ReadCursor();
                    pointerUnchanged = cursor.X == current.X && cursor.Y == current.Y;
                    focusUnchanged = foreground == Native.GetForegroundWindow();
                    physicalKeyUp = (new NativeKeyStateBackend().ReadAsync(135) & 0x8000) == 0;
                    if (ending == "cancel")
                    {
                        stop.Cancel();
                    }
                }, stop.Token);
                outcome = "completed";
            }
            catch (OperationCanceledException error) when (ending == "cancel" && error.CancellationToken == stop.Token)
            {
                outcome = "cancelled";
            }
            catch (ScriptException) when (ending == "error")
            {
                outcome = "script-error";
            }
            var next = new NativeInputOwnershipBackend();
            next.SetBlocked(true);
            next.SetBlocked(false);
            recovered = true;
        }
        catch (Exception error)
        {
            failure = error.ToString();
        }
        await File.WriteAllTextAsync(receipt + ".pending", JsonSerializer.Serialize(new
        {
            process_id = Environment.ProcessId,
            ending,
            dpiContext,
            dpi,
            outcome,
            blocked,
            pointerUnchanged,
            focusUnchanged,
            physicalKeyUp,
            recovered,
            failure
        }));
        File.Move(receipt + ".pending", receipt);
        await WaitAsync(receipt + ".exit");
    }

    private static void Gate(string marker, string acknowledgement)
    {
        File.WriteAllText(marker, "ready");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(acknowledgement))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("受信確認が届きません。");
            }
            Thread.Sleep(10);
        }
    }

    private static Native.Point ReadCursor()
    {
        if (!GetPhysicalCursorPos(out Native.Point point))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return point;
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(path))
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
