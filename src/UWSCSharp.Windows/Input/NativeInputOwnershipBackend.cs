using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有スレッド上でWin32の入力禁止と送信を実行します。
/// </summary>
internal sealed partial class NativeInputOwnershipBackend : IInputOwnershipBackend
{
    private readonly Func<bool, (bool Success, int Error)> block;
    private readonly Func<Native.Input, (uint Sent, int Error)>? send;

    internal NativeInputOwnershipBackend(
        Func<bool, (bool Success, int Error)>? block = null,
        Func<Native.Input, (uint Sent, int Error)>? send = null)
    {
        this.block = block ?? BlockNative;
        this.send = send;
    }

    /// <summary>
    /// 禁止または解除を要求し、OSの失敗を記録します。
    /// </summary>
    public void SetBlocked(bool blocked)
    {
        (bool success, int error) = block(blocked);
        if (success)
        {
            return;
        }

        string operation = blocked ? "入力禁止" : "入力禁止の解除";
        string detail = error == 0
            ? "OSから詳細なエラーコードは返されませんでした。"
            : new Win32Exception(error).Message;
        throw WindowsOperationErrors.Record(new Win32Exception(
            error, operation + "に失敗しました。" + detail));
    }

    /// <summary>
    /// 既存の入力送信処理で件数と失敗を確認します。
    /// </summary>
    public void Send(Native.Input input) => NativeInputSender.Send(input, send);

    private static (bool Success, int Error) BlockNative(bool blocked)
    {
        bool success = BlockInput(blocked);
        return (success, Marshal.GetLastWin32Error());
    }
}
