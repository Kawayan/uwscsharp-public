using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// アクセシビリティを使わず、APIで確認できるボタンだけを操作します。
/// </summary>
internal static partial class NativeButtonApi
{

    internal static bool Click(nint root, nint target, int request, int kind,
        bool managed, IListSelectionPlatform platform, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((kind & 0x3800) != 0)
        {
            return NativeButtonControl.Click(root, target, request,
                kind, platform, token);
        }

        int type = Native.GetWindowLongW(target, Native.GwlStyle) & 15;
        bool check = type is 2 or 3 or 5 or 6;
        bool radio = type is 4 or 9;
        bool push = type is 0 or 1 or 12 or 13 or 14 or 15
            || managed && type == 11 && IsManagedPush(target, token);
        if (!check && !radio && !push)
        {
            // 所有描画の状態を推測して反転させず、操作不能として返します。
            return false;
        }

        int desired = request is >= 0 and <= 2 ? request : 1;
        int state = check ? ReadState(target, token) : 0;
        if (check && desired == 2 && type is not (5 or 6) && state != desired)
        {
            return false;
        }

        bool background = (kind & (int)ClickItemFlags.Background) != 0;
        if (!background)
        {
            platform.Activate(root, token);
        }
        if ((kind & (int)ClickItemFlags.MouseMove) != 0)
        {
            Native.Rect bounds = platform.Bounds(target);
            platform.MoveCursor(new Native.Point(
                WindowsOperationErrors.Add(bounds.Left,
                    WindowsOperationErrors.Subtract(bounds.Right, bounds.Left) / 2),
                WindowsOperationErrors.Add(bounds.Top,
                    WindowsOperationErrors.Subtract(bounds.Bottom, bounds.Top) / 2)));
        }
        token.ThrowIfCancellationRequested();
        if (background && !managed && type is 0 or 1 or 3
            && NativeSelectionTarget.Label(target)
                == ControlClassNames.Uppercase.Button)
        {
            if (check ? state == desired : request == 0)
            {
                return true;
            }

            nint parent = Native.GetParent(target);
            if (parent == 0 || !Native.IsWindowEnabled(target))
            {
                return false;
            }

            if (check)
            {
                const uint setCheck = 0xf1;
                _ = Send(target, setCheck, desired, 0, token);
                if (ReadState(target, token) != desired)
                {
                    return false;
                }
            }

            // フォーカスを移さず、状態を確定してから親に通知します。
            const uint command = 0x111;
            nint id = unchecked((ushort)Native.GetDlgCtrlID(target));
            _ = Send(parent, command, id, target, token);
            return !check || ReadState(target, token) == desired;
        }

        if (!check)
        {
            if (request != 0)
            {
                _ = Send(target, 0xf5, 0, 0, token);
            }
            return true;
        }

        for (int attempt = 0; attempt < 2 && state != desired; attempt++)
        {
            _ = Send(target, 0xf5, 0, 0, token);
            int next = ReadState(target, token);
            if (next == state)
            {
                return false;
            }
            state = next;
        }
        return state == desired;
    }

    private static int ReadState(nint target, CancellationToken token)
    {
        nuint state = Send(target, 0xf0, 0, 0, token);
        return state <= 2 ? (int)state
            : throw WindowsOperationErrors.Record(new InvalidOperationException(
                "APIのチェック状態が範囲外です。"));
    }

    internal static bool IsManagedPush(nint target, CancellationToken token)
        => ManagedType(target, token).StartsWith("System.Windows.Forms.Button,",
            StringComparison.Ordinal);

    internal static bool IsManagedCheck(nint target, CancellationToken token)
    {
        string type = ManagedType(target, token);
        return type.StartsWith("System.Windows.Forms.CheckBox,", StringComparison.Ordinal)
            || type.StartsWith("System.Windows.Forms.RadioButton,", StringComparison.Ordinal);
    }

    private static string ManagedType(nint target, CancellationToken token)
    {
        uint message = RegisterWindowMessageW("WM_GETCONTROLTYPE");
        if (message == 0)
        {
            throw WindowsOperationErrors.Record(new Win32Exception(Marshal.GetLastWin32Error()));
        }
        nuint bytes = Send(target, message, 0, 0, token);
        if (bytes is < 2 or > 512 || (bytes & 1) != 0)
        {
            return "";
        }

        using var buffer = new NativeControlTextBuffer(target);
        buffer.Prepare();
        // 同期送信が戻るまで領域を保持し、取消や期限で先に解放しません。
        nint count = Native.SendMessageW(target, message, 256, buffer.TextAddress);
        token.ThrowIfCancellationRequested();
        return count is > 0 and <= 256 ? buffer.Read((int)count) : "";
    }

    private static nuint Send(nint target, uint message, nint parameter,
        nint value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Native.SendMessageTimeoutW(target, message, parameter, value,
            Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out nuint result) == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw WindowsOperationErrors.Record(new Win32Exception(error == 0 ? 1460 : error));
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
