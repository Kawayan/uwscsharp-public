using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static class NativeTabControl
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Item
    {
        public uint Mask;
        public uint State;
        public uint StateMask;
        public nint Text;
        public int Capacity;
        public int Image;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Notification
    {
        public nint Window;
        public nuint Id;
        public int Code;
    }

    internal static Task<List<string>> ReadAsync(nint target, CancellationToken token,
        Action<string>? onItem = null)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Read(target, token, onItem));
        }

        return NativeControlWorker.RunAsync(active => Read(target, active, onItem), token);
    }

    internal static Task<bool> SelectAsync(nint target, int index, CancellationToken token)
    {
        Native.GetWindowThreadProcessId(target, out uint process);
        if (process != Environment.ProcessId)
        {
            return UiaItemActions.SelectTabAsync(target, index, false, token);
        }

        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Select(target, index, token));
        }

        return NativeControlWorker.RunAsync(active => Select(target, index, active), token);
    }

    internal static List<string> Read(nint target, CancellationToken token,
        Action<string>? onItem = null)
    {
        token.ThrowIfCancellationRequested();
        nint count = Native.SendMessageW(target, 0x1304, 0, 0);
        if (count < 0 || count > UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TABの項目数が範囲外です。"));
        }

        using var buffer = new NativeControlTextBuffer(target);
        List<string> result = [];
        int characters = 0;
        for (int index = 0; index < (int)count; index++)
        {
            token.ThrowIfCancellationRequested();
            buffer.Prepare();
            // 同期messageが戻るまでworkerがbufferを保持します。
            if (Native.SendMessageW(target, 0x133c, index, buffer.ItemAddress) == 0)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TABの文字列取得に失敗しました。"));
            }

            token.ThrowIfCancellationRequested();
            string normalized = ButtonName.Normalize(buffer.Read());
            if (normalized.Length > NativeListControl.MaximumCharacters - characters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TABの文字列取得上限を超えました。"));
            }

            characters += normalized.Length;
            result.Add(normalized);
            onItem?.Invoke(normalized);
        }

        return result;
    }

    internal static bool Click(nint target, int index, CancellationToken token,
        NativeTabClickBuffer shared, nint root, IListSelectionPlatform platform, int kind)
    {
        if (!Select(target, index, token, root, platform, (kind & 512) != 0,
            allowUia: (kind & (int)ClickItemFlags.Api) == 0))
        {
            return false;
        }

        token.ThrowIfCancellationRequested();
        if ((kind & 0x3c00) == 0)
        {
            return true;
        }

        Native.Rect rectangle = shared.ReadRectangle(target, index, token);
        if ((kind & 1024) != 0)
        {
            int width = WindowsOperationErrors.Subtract(rectangle.Right, rectangle.Left);
            int height = WindowsOperationErrors.Subtract(rectangle.Bottom, rectangle.Top);
            Native.Point screen = platform.ToScreen(target,
                new Native.Point(rectangle.Left, rectangle.Top));
            token.ThrowIfCancellationRequested();
            platform.MoveCursor(new Native.Point(WindowsOperationErrors.Add(screen.X, width / 2),
                WindowsOperationErrors.Add(screen.Y, height / 2)));
            token.ThrowIfCancellationRequested();
        }

        if ((kind & 0x3800) != 0)
        {
            // TABのcallerはX/YをともにWORDへ切り詰めて共通helperへ渡します。
            NativeControlMouse.Post(target, kind, new Native.Point(
                unchecked((ushort)rectangle.Left), unchecked((ushort)rectangle.Top)),
                platform, token);
        }

        return true;
    }

    private static nuint NotificationId(nint target)
    {
        int id = Native.GetDlgCtrlID(target);
        if (id < 0)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TABのcontrol IDが範囲外です。"));
        }

        return (nuint)id;
    }

    internal static bool Select(nint target, int index, CancellationToken token,
        nint root = 0, IListSelectionPlatform? platform = null, bool back = false,
        bool allowUia = true)
    {
        token.ThrowIfCancellationRequested();
        nint parent = root == 0 ? Native.GetAncestor(target, 2) : root;
        Native.GetWindowThreadProcessId(parent, out uint process);
        if (process != Environment.ProcessId)
        {
            if (allowUia)
            {
                // 別プロセスへWM_NOTIFYを偽装せず、対象の選択パターンを使います。
                return UiaItemActions.SelectTabAsync(target, index, back, token)
                    .GetAwaiter().GetResult();
            }

            throw WindowsOperationErrors.Record(
                new NotSupportedException("別プロセスTABのWM_NOTIFYは未対応です。"));
        }
        var preparation = new NativeListSelectionBackend(parent, target,
            combo: false, back: back, platform: platform);
        if (!back)
        {
            preparation.ActivateRoot(token);
        }

        var notification = new Notification
        {
            Window = target,
            Id = NotificationId(target),
            Code = -552
        };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Notification>());
        try
        {
            Marshal.StructureToPtr(notification, pointer, false);
            // 原版同様、SELCHANGINGの戻り値は選択拒否に使用しません。
            Native.SendMessageW(parent, 0x4e, (nint)notification.Id, pointer);
            token.ThrowIfCancellationRequested();
            preparation.Yield(20, token);

            Native.SendMessageW(target, 0x1330, index, 0);
            Native.SendMessageW(target, 0x130c, index, 0);
            notification.Id = NotificationId(target);
            notification.Code = -551;
            Marshal.StructureToPtr(notification, pointer, false);
            Native.SendMessageW(parent, 0x4e, (nint)notification.Id, pointer);
            token.ThrowIfCancellationRequested();
            preparation.Yield(20, token);
            return Native.SendMessageW(target, 0x130b, 0, 0) == index;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
