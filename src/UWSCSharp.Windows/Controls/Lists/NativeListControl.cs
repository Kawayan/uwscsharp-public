using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static partial class NativeListControl
{
    internal const int MaximumCharacters = 16_777_216;

    private const int SelectedItemCharacters = 0x800;

    private const int EnumeratedItemCharacters = 0xfde9;

    internal static string Trim(string text)
    {
        int start = 0;
        int end = text.Length;
        while (start < end && text[start] <= ' ')
        {
            start++;
        }

        while (end > start && text[end - 1] <= ' ')
        {
            end--;
        }

        return text[start..end];
    }

    private static bool IsCombo(nint target)
    {
        return NativeSelectionTarget.Kind(NativeSelectionTarget.Label(target))
            == NativeSelectionKind.Combo;
    }

    private static bool HasItemData(int style, bool combo) =>
        (!combo && (style & 0x2000) != 0)
        || ((style & 0x30) != 0 && (style & (combo ? 0x200 : 0x40)) == 0);

    internal static Task<List<string>> ReadAsync(nint target, CancellationToken token,
        Action<string>? onItem = null, ListEnumerationBuffer? enumerationBuffer = null)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Read(target, token, onItem: onItem, enumerationBuffer: enumerationBuffer));
        }

        return NativeControlWorker.RunAsync(active => Read(target, active, onItem: onItem, enumerationBuffer: enumerationBuffer), token);
    }

    internal static Task<bool> SelectAsync(nint target, string name, CancellationToken token,
        nint root = 0, bool back = true, IListSelectionPlatform? platform = null,
        bool move = false, Action? onMultipleMatch = null, bool partial = false,
        Func<bool>? acceptSingleMatch = null)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Select(target, name, token, root, back, platform, move, onMultipleMatch, partial, acceptSingleMatch));
        }

        return NativeControlWorker.RunAsync(active => Select(target, name, active, root, back, platform, move, onMultipleMatch, partial, acceptSingleMatch), token);
    }

    internal static List<string> Read(nint target, CancellationToken token,
        Action<string>? onItem = null, ListEnumerationBuffer? enumerationBuffer = null) =>
        ReadItems(target, token, EnumeratedItemCharacters, enumeration: true,
            onItem: onItem, enumerationBuffer: enumerationBuffer);

    internal static List<string> ReadForNameMatching(nint target,
        CancellationToken token, Func<string, bool>? stopAfterItem = null) =>
        ReadItems(target, token, SelectedItemCharacters, enumeration: false,
            stopAfterItem: stopAfterItem);

    private static List<string> ReadItems(nint target, CancellationToken token,
        int itemCharacters, bool enumeration,
        Action<string>? onItem = null, ListEnumerationBuffer? enumerationBuffer = null,
        Func<string, bool>? stopAfterItem = null)
    {
        enumerationBuffer ??= new ListEnumerationBuffer(initialCharacters: itemCharacters);
        bool combo = IsCombo(target);
        int style = Native.GetWindowLongW(target, -16);
        nint count = Message(target, combo ? 0x146u : 0x18bu, token);
        if (count < 0)
        {
            // 原版のcount-1は通常の負数を空結果とし、最小整数で溢れます。
            _ = WindowsOperationErrors.Subtract(WindowsOperationErrors.Int32(count), 1);
            return [];
        }

        if (count < 0 || count > UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("LISTの項目数が範囲外です。"));
        }

        List<string> items = [];
        int characters = 0;
        for (int i = 0; i < (int)count; i++)
        {
            string text;
            if (HasItemData(style, combo))
            {
                text = ReadItemData(target, combo, i, token, enumerationBuffer, itemCharacters, enumeration);
            }
            else
            {
                text = ReadItem(target, combo, i, MaximumCharacters - characters, token,
                    enumerationBuffer, itemCharacters, enumeration);
            }

            if (text.Length > itemCharacters)
            {
                text = text[..itemCharacters];
            }

            if (text.Length > MaximumCharacters - characters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("LISTの取得文字数が範囲外です。"));
            }

            characters += text.Length;
            items.Add(text);
            onItem?.Invoke(text);
            if (stopAfterItem?.Invoke(text) == true)
            {
                break;
            }
        }

        return items;
    }

    private static string ReadItem(nint target, bool combo, nint index,
        int remaining, CancellationToken token, ListEnumerationBuffer? enumerationBuffer = null,
        int scanCharacters = EnumeratedItemCharacters, bool requirePositiveList = true)
    {
        if (enumerationBuffer is not null)
        {
            return ReadBufferedItem(target, combo, index, token, enumerationBuffer, scanCharacters, requirePositiveList);
        }

        nint length = Message(target, combo ? 0x149u : 0x18au, token, index);
        if (length < 0 || length > remaining)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("LISTの文字列長が範囲外です。"));
        }

        var buffer = new StringBuilder((int)length + 1);
        token.ThrowIfCancellationRequested();
        if (Native.ReadText(target, combo ? 0x148u : 0x189u,
            index, buffer, Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out nuint copied) == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        token.ThrowIfCancellationRequested();
        if (unchecked((nint)copied) < 0 || copied > (nuint)length)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("LISTの文字列取得に失敗しました。"));
        }

        return buffer.ToString();
    }

    private static string ReadBufferedItem(nint target, bool combo, nint index,
        CancellationToken token, ListEnumerationBuffer enumerationBuffer, int scanCharacters,
        bool requirePositiveList)
    {
        token.ThrowIfCancellationRequested();
        byte[] initial = enumerationBuffer.Prepare(scanCharacters);
        // 長さ照会を行わず、既存の最大文字数を収容する仮想領域を確保します。
        nint pointer = VirtualAlloc(0, (nuint)((MaximumCharacters + 1) * 2), 0x3000, 4);
        if (pointer == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            Marshal.Copy(initial, 0, pointer, initial.Length);
            // timeoutでcallerが復帰してもnative復帰までworkerが所有します。
            int copied = unchecked((int)Native.SendMessageW(target,
                combo ? 0x148u : 0x189u, index, pointer));
            token.ThrowIfCancellationRequested();
            Marshal.Copy(pointer, initial, 0, initial.Length);
            enumerationBuffer.Capture(initial, copied);
            return requirePositiveList && !combo && copied <= 0
                ? "" : enumerationBuffer.Read(scanCharacters);
        }
        finally
        {
            VirtualFree(pointer, 0, 0x8000);
        }
    }

    internal static bool Select(nint target, string name, CancellationToken token,
        nint root = 0, bool back = true, IListSelectionPlatform? platform = null,
        bool move = false, Action? onMultipleMatch = null, bool partial = false,
        Func<bool>? acceptSingleMatch = null, Action? onSingleMatch = null,
        ClickItemFlags mouseFlags = 0)
    {
        bool combo = IsCombo(target);
        mouseFlags |= move ? ClickItemFlags.MouseMove : 0;

        int style = Native.GetWindowLongW(target, -16);
        if (!combo && (style & 0x808) != 0)
        {
            return SelectMultiple(target, name, token, root, back, platform,
                onMultipleMatch, partial, mouseFlags);
        }

        int itemIndex = -1;
        int index = -1;
        bool Visit(string item)
        {
            itemIndex++;
            if (!NativeListNameMatcher.Matches(item, name, partial)
                || (acceptSingleMatch is not null && !acceptSingleMatch()))
            {
                return false;
            }

            index = itemIndex;
            return true;
        }

        ReadForNameMatching(target, token, stopAfterItem: Visit);
        if (index < 0)
        {
            return false;
        }

        onSingleMatch?.Invoke();
        var backend = new NativeListSelectionBackend(
            root == 0 ? Native.GetAncestor(target, 2) : root, target, combo, back, platform, move);
        if (!combo && (mouseFlags & ProviderItemMouse.Options) != 0)
        {
            if (!back)
            {
                backend.ActivateRoot(token);
            }

            NativeListMouse.Apply(target, index, mouseFlags, platform, token);
            if ((mouseFlags & ProviderItemMouse.Buttons) != 0)
            {
                return true;
            }
        }

        return ListSelectionSequence.Execute(index, combo, back, backend, token);
    }

    internal static Task<string> SelectedAsync(nint target, CancellationToken token)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(SelectedText(target, token));
        }

        return NativeControlWorker.RunAsync(active => SelectedText(target, active), token);
    }

    internal static string SelectedText(nint target, CancellationToken token)
    {
        bool combo = IsCombo(target);
        if (combo)
        {
            // Comboは選択番号を先に照会し、未選択なら文字列を取得しません。
            int index = unchecked((int)Message(target, 0x147, token));
            if (index < 0)
            {
                return "";
            }

            return ReadSelectedItem(target, true, index, token);
        }

        int count = unchecked((int)Message(target, 0x18b, token));
        if (count <= 0)
        {
            return "";
        }

        if (count > UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("LISTの項目数が範囲外です。"));
        }

        return JoinSelection(SelectedItems(target, (int)count, token), token);
    }

    private static IEnumerable<string> SelectedItems(nint target,
        int count, CancellationToken token)
    {
        for (int index = 0; index < count; index++)
        {
            // 原版は単一/複数を分けずLB_GETSELの非ゼロを取得条件にします。
            nint state = Message(target, 0x187, token, index);
            if (state != 0)
            {
                yield return ReadSelectedItem(target, false, index, token);
            }
        }
    }

    private static string ReadSelectedItem(nint target, bool combo,
        nint index, CancellationToken token)
    {
        int style = Native.GetWindowLongW(target, -16);
        if (HasItemData(style, combo))
        {
            return ReadItemData(target, combo, index, token);
        }

        string text = ReadItem(target, combo, index, MaximumCharacters, token);
        // 634428は取得済みbufferを最大800h UTF-16単位で文字列化します。
        return text.Length > SelectedItemCharacters
            ? text[..SelectedItemCharacters] : text;
    }

    private static string ReadItemData(nint target, bool combo,
        nint index, CancellationToken token, ListEnumerationBuffer? enumerationBuffer = null,
        int scanCharacters = EnumeratedItemCharacters, bool requirePositiveList = true)
    {
        nint buffer = Marshal.AllocHGlobal(8);
        try
        {
            // native item dataを値として読み、文字列ポインターとして参照しません。
            Marshal.WriteInt64(buffer, 0);
            if (enumerationBuffer is not null)
            {
                Marshal.Copy(enumerationBuffer.Prepare(4), 0, buffer, 8);
            }

            token.ThrowIfCancellationRequested();
            // 別threadではworkerがnative復帰までbufferを所有します。
            nint copied = Native.SendMessageW(target,
                combo ? 0x148u : 0x189u, index, buffer);
            token.ThrowIfCancellationRequested();
            uint data = unchecked((uint)Marshal.ReadInt32(buffer));
            if (enumerationBuffer is not null)
            {
                enumerationBuffer.WriteItemData(data);
                return requirePositiveList && !combo && unchecked((int)copied) <= 0
                    ? "" : enumerationBuffer.Read(scanCharacters);
            }

            // 同threadのx64は8、標準messageの別thread配送では4も返ります。
            if (copied != 4 && copied != 8)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException($"item dataの取得に失敗しました（返値: {copied}）。"));
            }

            char first = (char)(data & 0xffff);
            if (first == '\0')
            {
                return "";
            }

            if ((data >> 16) == 0)
            {
                return new string(first, 1);
            }

            throw WindowsOperationErrors.Record(
                new NotSupportedException(
                "原版x86のitem data内で終端しない文字列は未対応です。"));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string JoinSelection(IEnumerable<string> items,
        CancellationToken token)
    {
        var result = new StringBuilder();
        foreach (string text in items)
        {
            token.ThrowIfCancellationRequested();
            int separator = result.Length > 0 ? 1 : 0;
            if ((long)result.Length + separator + text.Length > MaximumCharacters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("選択文字列が長すぎます。"));
            }

            // 原版は先頭の空項目では区切りを蓄積しません。
            if (separator != 0)
            {
                result.Append('\t');
            }

            result.Append(text);
        }

        return result.ToString();
    }

    private static bool SelectMultiple(nint target, string names,
        CancellationToken token, nint root, bool back, IListSelectionPlatform? platform,
        Action? onMultipleMatch, bool partial, ClickItemFlags mouseFlags)
    {
        var matcher = new MultipleListMatcher(names, partial);
        int index = -1;
        int matched = 0;
        bool Visit(string item)
        {
            index++;
            if (!matcher.Matches(item))
            {
                return false;
            }

            if (matched == 0)
            {
                onMultipleMatch?.Invoke();
                Message(target, 0x185, token, 0, -1);
                if (matcher.Count == 1)
                {
                    Message(target, 0x186, token, index);
                }
            }

            // setter返値ではなく、一致した項目の件数で終了を判定します。
            Message(target, 0x185, token, 1, index);
            matched++;
            return matched == matcher.Count;
        }

        ReadForNameMatching(target, token, stopAfterItem: Visit);
        bool success = matched > 0 && matched == matcher.Count;
        if (success && !back)
        {
            var backend = new NativeListSelectionBackend(
                root == 0 ? Native.GetAncestor(target, 2) : root,
                target, false, back, platform);
            backend.ActivateRoot(token);
        }

        if (success && (mouseFlags & ProviderItemMouse.Options) != 0)
        {
            NativeListMouse.Apply(target, index, mouseFlags, platform, token);
        }

        return success;
    }

    private static nint Message(nint target, uint message,
        CancellationToken token, nint wParam = 0, nint lParam = 0)
    {
        token.ThrowIfCancellationRequested();
        if (Native.SendMessageTimeoutW(target, message, wParam, lParam,
            Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out nuint result) == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        token.ThrowIfCancellationRequested();
        return unchecked((nint)result);
    }
}
