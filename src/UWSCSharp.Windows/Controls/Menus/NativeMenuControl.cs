using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeMenuControl
{
    internal readonly record struct Item(string Name, string Path, uint Id, bool Enabled, nint Menu, int Index);

    internal static List<string> ReadTopLevel(nint root, bool includeDisabled,
        CancellationToken token, Action<string>? onItem = null)
    {
        List<string> result = [];
        if (root == 0 || !Native.IsWindow(root))
        {
            return result;
        }

        nint menu = GetMenu(root);
        if (menu == 0)
        {
            return result;
        }

        int count = GetMenuItemCount(menu);
        if (count < 0 || count > 100000)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("menuの項目数が範囲外です。"));
        }

        int characters = 0;
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x141 };
            if (!GetMenuItemInfoW(menu, (uint)index, true, ref info))
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (!includeDisabled && (info.State & 3) != 0)
            {
                continue;
            }

            if ((info.Type & 0x100) != 0 || info.Length > 16384)
            {
                throw WindowsOperationErrors.Record(
                    new NotSupportedException("owner drawまたは長すぎるmenu文字列は未対応です。"));
            }

            uint capacity = info.Length + 1;
            nint pointer = Marshal.AllocHGlobal((int)capacity * 2);
            try
            {
                info.Text = pointer;
                info.Length = capacity;
                if (!GetMenuItemInfoW(menu, (uint)index, true, ref info) || info.Length >= capacity)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("menu文字列を取得できないか変更されました。"));
                }

                string name = ButtonName.Normalize(Marshal.PtrToStringUni(pointer, (int)info.Length));
                if (name.Length > NativeListControl.MaximumCharacters - characters)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("menu文字列の取得上限を超えました。"));
                }

                characters += name.Length;
                result.Add(name);
                onItem?.Invoke(name);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static IEnumerable<Item> Read(nint root, bool includeDisabled,
        CancellationToken token)
    {
        if (root == 0 || !Native.IsWindow(root))
        {
            yield break;
        }

        nint menu = GetMenu(root);
        if (menu == 0)
        {
            yield break;
        }

        Message(root, 0x116, menu, 0, token);
        foreach (Item item in Walk(root, menu, "", true, includeDisabled, [], token))
        {
            yield return item;
        }
    }

    // 記録時は対象アプリのメニュー初期化処理を呼びません。
    internal static Item? FindRecordingItem(nint root, uint command)
    {
        nint menu = GetMenu(root);
        if (menu == 0)
        {
            return null;
        }

        const int maximumItems = 4096;
        Item[] items = [.. Walk(0, menu, "", true, true, [], default)
            .Take(maximumItems + 1)];
        if (items.Length > maximumItems)
        {
            return null;
        }

        Item[] matches = [.. items.Where(item => item.Id == command)];
        if (matches.Length != 1 || !matches[0].Enabled)
        {
            return null;
        }

        Item match = matches[0];
        return items.Count(item => string.Equals(item.Path, match.Path,
            StringComparison.OrdinalIgnoreCase)) == 1 ? match : null;
    }

    private static IEnumerable<Item> Walk(nint root, nint menu, string parent,
        bool parentEnabled, bool includeDisabled, HashSet<nint> ancestors,
        CancellationToken token)
    {
        if (ancestors.Count == 64 || !ancestors.Add(menu))
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("menu階層が循環しているか深すぎます。"));
        }

        try
        {
            int count = GetMenuItemCount(menu);
            if (count < 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (count > 100000)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("menuの項目数が上限を超えました。"));
            }

            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var info = new MenuItemInfo { Size = (uint)Marshal.SizeOf<MenuItemInfo>(), Mask = 0x147 };
                if (!GetMenuItemInfoW(menu, (uint)index, true, ref info))
                {
                    throw WindowsOperationErrors.Record(
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                bool enabled = parentEnabled && (info.State & 3) == 0;
                if ((!includeDisabled && !enabled) || (info.Type & 0x904) != 0)
                {
                    continue;
                }

                if (info.Length > 16384)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("menu文字列が上限を超えました。"));
                }

                nint buffer = Marshal.AllocHGlobal(checked(((int)info.Length + 1) * 2));
                string name;
                try
                {
                    uint capacity = info.Length + 1;
                    info.Text = buffer;
                    info.Length = capacity;
                    if (!GetMenuItemInfoW(menu, (uint)index, true, ref info))
                    {
                        throw WindowsOperationErrors.Record(
                            new Win32Exception(Marshal.GetLastWin32Error()));
                    }

                    if (info.Length >= capacity)
                    {
                        throw WindowsOperationErrors.Record(
                            new InvalidOperationException("menu文字列が取得中に変わりました。"));
                    }

                    name = ButtonName.Normalize(Marshal.PtrToStringUni(buffer, (int)info.Length));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (name.Length == 0 || root == 0 && name.Contains('\\'))
                {
                    continue;
                }

                string path = parent.Length == 0 ? name : parent + "\\" + name;
                if (path.Length > 16384)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("menu pathが上限を超えました。"));
                }

                if (info.Submenu != 0)
                {
                    if (root != 0)
                    {
                        Message(root, 0x117, info.Submenu, index & 0xffff, token);
                    }

                    foreach (Item child in Walk(root, info.Submenu, path, enabled,
                        includeDisabled, ancestors, token))
                    {
                        yield return child;
                    }
                }
                else
                {
                    yield return new(name, path, info.Id, enabled, menu, index);
                }
            }
        }
        finally
        {
            ancestors.Remove(menu);
        }
    }

    internal static bool Invoke(nint root, string name, int occurrence, CancellationToken token,
        bool on = true, IListSelectionPlatform? platform = null, bool back = false,
        bool partial = false, Action? onMatch = null)
    {
        token.ThrowIfCancellationRequested();
        if (root == 0 || !Native.IsWindow(root))
        {
            return false;
        }

        nint menu = 0;
        try
        {
            menu = GetMenu(root);
            if (menu != 0)
            {
                Message(root, 0x116, menu, 0, token);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && !token.IsCancellationRequested)
        {
            // 原版のMENU取得・初期化の局所捕捉は候補なしとして扱います。
            menu = 0;
        }

        if (menu == 0)
        {
            return false;
        }

        string desired = ButtonName.Normalize(NativeListNameMatcher.AsciiUpper(
            partial ? name : NativeListControl.Trim(name)));
        string[] segments = NativeTreePath.Split(desired);
        var search = new NativeMenuClickSearch(root, occurrence, on,
            platform ?? new NativeListSelectionPlatform(), back, partial, token, onMatch);
        if (segments.Length <= 1)
        {
            return search.Find(menu, desired, 0) != 0;
        }

        for (int index = 0; index < segments.Length && menu != 0; index++)
        {
            menu = search.Find(menu, segments[index], index == segments.Length - 1 ? 9 : 1);
        }

        return menu != 0;
    }

    internal static nint? InvokeMatch(nint root, nint menu, int index,
        CancellationToken token, bool on, IListSelectionPlatform operations, bool back)
    {
        var preparation = new NativeListSelectionBackend(root, root,
            combo: false, back: back, platform: operations);
        if (!back)
        {
            preparation.ActivateRoot(token);
        }

        try
        {
            if (GetMenu(root) != menu)
            {
                Message(root, 0x117, menu, unchecked((ushort)index), token);
                preparation.Yield(4, token);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && !token.IsCancellationRequested)
        {
            // submenu初期化だけの失敗では、後続のSTATE照会を継続します。
        }

        token.ThrowIfCancellationRequested();
        // 原版は一致直後のSTATE取得のBOOL返値では分岐しません。
        var state = new MenuItemInfo
        {
            Size = (uint)Marshal.SizeOf<MenuItemInfo>(),
            Mask = 1
        };
        _ = GetMenuItemInfoW(menu, (uint)index, true, ref state);
        token.ThrowIfCancellationRequested();
        if ((state.State & 3) != 0)
        {
            return null;
        }

        if (((state.State & 8) != 0) == on)
        {
            return 1;
        }

        preparation.Yield(10, token);
        token.ThrowIfCancellationRequested();
        uint command = GetMenuItemID(menu, index);
        operations.Post(root, 0x111, unchecked((nint)command), 0);
        token.ThrowIfCancellationRequested();
        return unchecked((nint)GetMenuItemID(menu, index));
    }

    internal static void Message(nint root, uint message, nint wParam, nint lParam,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Native.SendMessageTimeoutW(root, message, wParam, lParam, Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out _) == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        token.ThrowIfCancellationRequested();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuItemInfo
    {
        internal uint Size;
        internal uint Mask;
        internal uint Type;
        internal uint State;
        internal uint Id;
        internal nint Submenu;
        internal nint CheckedBitmap;
        internal nint UncheckedBitmap;
        internal nuint Data;
        internal nint Text;
        internal uint Length;
        internal nint Bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuInfo
    {
        internal uint Size;
        internal uint Mask;
        internal uint Style;
        internal uint MaximumHeight;
        internal nint Background;
        internal uint HelpId;
        internal nuint Data;
    }
}
