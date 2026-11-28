using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static class NativeInputSender
{
    internal static bool TryCreateAbsoluteMouseMove(Native.Point target, out Native.Input input)
    {
        const int range = 65_536;
        const uint move = 0x0001;
        const uint noCoalesce = 0x2000;
        const uint virtualDesktop = 0x4000;
        const uint absolute = 0x8000;
        long x = (long)target.X - Native.GetSystemMetrics(76);
        long y = (long)target.Y - Native.GetSystemMetrics(77);
        int width = Native.GetSystemMetrics(78);
        int height = Native.GetSystemMetrics(79);
        if (x < 0 || x >= width || y < 0 || y >= height || width > range || height > range)
        {
            input = default;
            return false;
        }

        // ピクセルの左端を切り上げ、絶対入力の丸めで隣の座標へずれないようにします。
        input = new Native.Input
        {
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput
                {
                    X = (int)((x * range + width - 1) / width),
                    Y = (int)((y * range + height - 1) / height),
                    Flags = move | noCoalesce | virtualDesktop | absolute,
                    Extra = 1
                }
            }
        };
        return true;
    }

    internal static void Send(Native.Input input,
        Func<Native.Input, (uint Sent, int Error)>? sender = null)
    {
        (uint sent, int error) = sender is null
            ? SendNative(input) : sender(input);
        if (sent == 1)
        {
            return;
        }

        // UIPIなどでは詳細コードが得られなくても送信は失敗しています。
        string detail = error == 0
            ? "OSから詳細なエラーコードは返されませんでした。"
            : new Win32Exception(error).Message;
        throw WindowsOperationErrors.Record(
            new Win32Exception(error,
            "入力送信に失敗しました。" + detail
            + " 入力先の権限やデスクトップの状態を確認してください。"));
    }

    private static (uint Sent, int Error) SendNative(Native.Input input)
    {
        uint sent = Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>());
        return (sent, Marshal.GetLastWin32Error());
    }
}
