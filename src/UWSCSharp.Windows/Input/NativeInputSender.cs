using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static class NativeInputSender
{
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
