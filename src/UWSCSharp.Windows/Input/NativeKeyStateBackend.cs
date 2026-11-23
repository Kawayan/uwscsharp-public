using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 入力を送らず、Win32のキー状態とIME状態を取得します。
/// </summary>
internal sealed partial class NativeKeyStateBackend : IKeyStateBackend
{
    internal readonly record struct ImeReply(bool Success, nuint State, int Error);

    private readonly Func<nint> foreground;
    private readonly Func<nint, nint> imeWindow;
    private readonly Func<nint, ImeReply> query;

    internal NativeKeyStateBackend()
        : this(Native.GetForegroundWindow, ImmGetDefaultIMEWnd, QueryIme)
    {
    }

    internal NativeKeyStateBackend(Func<nint> foreground,
        Func<nint, nint> imeWindow, Func<nint, ImeReply> query)
    {
        this.foreground = foreground;
        this.imeWindow = imeWindow;
        this.query = query;
    }

    /// <summary>
    /// 物理キーの押下中と直近押下のビットを取得します。
    /// </summary>
    public short ReadAsync(int key) => Native.GetAsyncKeyState(key);

    /// <summary>
    /// 呼出しスレッドのトグル状態を取得します。
    /// </summary>
    public short ReadToggle(int key) => Native.GetKeyState(key);

    /// <summary>
    /// 前面ウィンドウのIME開閉状態を期限付きのメッセージで取得します。
    /// </summary>
    public bool ReadIme(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint window = foreground();
        if (window == 0)
        {
            return false;
        }

        nint ime = imeWindow(window);
        if (ime == 0)
        {
            return false;
        }

        token.ThrowIfCancellationRequested();
        ImeReply reply = query(ime);
        if (!reply.Success)
        {
            token.ThrowIfCancellationRequested();
            int error = reply.Error;
            if (error == 1460)
            {
                throw WindowsOperationErrors.Record(new TimeoutException(
                    "GETKEYSTATEのIME状態取得が期限を超えました。"));
            }

            if (error == 0)
            {
                throw WindowsOperationErrors.Record(new InvalidOperationException(
                    "GETKEYSTATEのIME状態を取得できませんでした（詳細エラーなし）。"));
            }

            throw WindowsOperationErrors.Record(new Win32Exception(error));
        }

        token.ThrowIfCancellationRequested();
        return reply.State != 0;
    }

    private static ImeReply QueryIme(nint window)
    {
        nint success = Native.SendMessageTimeoutW(window, 0x0283, 5, 0,
            3, 1000, out nuint state);
        return new(success != 0, state,
            success == 0 ? Marshal.GetLastWin32Error() : 0);
    }
}
