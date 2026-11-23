using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static class NativeListViewControl
{
    private static bool IsCheckedImage(int image) =>
        image != 0 && ((image - 1) & 1) != 0;

    internal static Task<(int Matches, bool Found, bool Success)> ClickAsync(
        nint target, string label, int occurrence, bool on, CancellationToken token)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Click(target, label, occurrence, on, token));
        }

        return NativeControlWorker.RunAsync(active => Click(target,
            label, occurrence, on, active), token);
    }

    internal static (int Matches, bool Found, bool Success) Click(nint target,
        string label, int occurrence, bool on, CancellationToken token,
        NativeListViewClickBuffer? shared = null,
        IListSelectionPlatform? platform = null, bool back = false, int kind = 32,
        Action<int>? readCheck = null, Action? onAttempt = null)
    {
        if (shared is not null)
        {
            return ClickCore(target, label, occurrence, on, token, shared, platform, back, kind, readCheck, onAttempt);
        }

        using var owner = new NativeControlBufferOwner(Native.GetAncestor(target, 2));
        return ClickCore(target, label, occurrence, on, token,
            new NativeListViewClickBuffer(owner), platform, back, kind, readCheck, onAttempt);
    }

    private static (int Matches, bool Found, bool Success) ClickCore(nint target,
        string label, int occurrence, bool on, CancellationToken token,
        NativeListViewClickBuffer shared, IListSelectionPlatform? platform, bool back, int kind,
        Action<int>? readCheck, Action? onAttempt)
    {
        token.ThrowIfCancellationRequested();
        IListSelectionPlatform operations = platform ?? new NativeListSelectionPlatform();
        int matches = 0;
        int chosen = -1;
        _ = shared.VisitNames(target, token, (index, name) =>
        {
            bool selected = NativeListNameMatcher.Matches(name, label, false)
                && ++matches == occurrence;
            if (selected)
            {
                chosen = index;
            }

            int state = selected ? 3 : 0;
            onAttempt?.Invoke();
            _ = shared.SetState(target, index, state, token,
                // 原版は毎行Write後にCTRLのSHORT値を判定し、setter返値は無視します。
                suppressClear: () => (operations.KeyState(17) & ~0xf) != 0);
            return true;
        });

        if (chosen < 0)
        {
            return (matches, false, false);
        }

        Native.Point first = shared.ReadFirstPosition(target, token);
        Native.Point selected = shared.ReadSelectedPosition(target, chosen, token);
        NotifyNeighbors(shared.Root, target, first.Y == selected.Y ? 0 : chosen,
            back, operations, token);
        int image = StateImage(target, chosen, token);
        // 照会は隣接通知後に状態を返し、通常actionには進みません。
        if (readCheck is not null)
        {
            readCheck(IsCheckedImage(image) ? 1 : 0);
            return (matches, true, true);
        }

        try
        {
            Native.Point point = shared.ReadSelectedPosition(target, chosen, token);
            if ((kind & 0x3800) != 0)
            {
                NativeControlMouse.Post(target, kind,
                    new Native.Point(WindowsOperationErrors.Add(point.X, 4),
                        WindowsOperationErrors.Add(point.Y, 4)),
                    operations, token);
            }
            else if (image == 0 && on)
            {
                var keys = new NativeListSelectionBackend(shared.Root, target,
                    combo: false, back: back, platform: operations);
                keys.PressKey(13, token);
            }
            else if (image != 0 && IsCheckedImage(image) != on)
            {
                var keys = new NativeListSelectionBackend(shared.Root, target,
                    combo: false, back: back, platform: operations);
                keys.PressKey(32, token);
            }

            if ((kind & 1024) != 0)
            {
                // VIEWは通知後、元のPOINTをscreen変換してから5px加算します。
                Native.Point screen = operations.ToScreen(target, point);
                token.ThrowIfCancellationRequested();
                operations.MoveCursor(new Native.Point(
                    WindowsOperationErrors.Add(screen.X, 5), WindowsOperationErrors.Add(screen.Y, 5)));
                token.ThrowIfCancellationRequested();
            }

            return (matches, true, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && !token.IsCancellationRequested)
        {
            // VIEWは操作区間の例外を捕捉すると不成功へ変換します。
            return (matches, true, false);
        }
    }

    private static void NotifyNeighbors(nint root, nint target, int index,
        bool back, IListSelectionPlatform? platform, CancellationToken token)
    {
        var backend = new NativeListSelectionBackend(root, target, false, back, platform);
        if (!back)
        {
            backend.ActivateRoot(token);
        }

        backend.PrepareRoot(token);
        try
        {
            backend.LockUpdates(true);
            backend.PressKey(index == 0 ? 40 : 38, token);
            backend.PressKey(index == 0 ? 38 : 40, token);
        }
        finally
        {
            // キー通知中の取消や例外でも描画lockを解除します。
            backend.LockUpdates(false);
        }

        backend.Yield(20, token);
        backend.PrepareRoot(token);
    }

    private static int StateImage(nint target, int item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint state = Native.SendMessageW(target, 0x102c, item, 0xf000);
        token.ThrowIfCancellationRequested();
        return ((int)state >> 12) & 3;
    }

    internal static Task<(int Matches, bool Found, int Value)> CheckAsync(
        nint target, string label, int occurrence, CancellationToken token)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Check(target, label, occurrence, token));
        }

        return NativeControlWorker.RunAsync(active => Check(target,
            label, occurrence, active), token);
    }

    internal static (int Matches, bool Found, int Value) Check(nint target,
        string label, int occurrence, CancellationToken token,
        NativeListViewClickBuffer? shared = null, IListSelectionPlatform? platform = null)
    {
        int value = -1;
        (int matches, bool found, bool success) = Click(target, label, occurrence,
            false, token, shared, platform, readCheck: state => value = state);
        return (matches, found, success ? value : -1);
    }

    internal static Task<List<string>> ReadAsync(nint target, int column,
        CancellationToken token, bool selected = false, Action<string>? onItem = null)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Read(target, column, token, selected, enumeration: !selected, onItem: onItem));
        }

        return NativeControlWorker.RunAsync(active => Read(target, column, active, selected, enumeration: !selected, onItem: onItem), token);
    }

    internal static async Task<string> SelectedAsync(nint target, int column,
        CancellationToken token)
    {
        // 選択取得は負列を先頭列へ補正し、減算overflowは伝播します。
        int selectedColumn = Math.Max(0, WindowsOperationErrors.Subtract(column, 1)) + 1;
        List<string> items = await ReadAsync(target, selectedColumn, token, selected: true).ConfigureAwait(false);
        return NativeListControl.JoinSelection(items, token);
    }

    private static List<string> Read(nint target, int column,
        CancellationToken token, bool selected, bool enumeration = false,
        Action<string>? onItem = null)
    {
        token.ThrowIfCancellationRequested();
        if ((Native.GetWindowLongW(target, -16) & 0x1000) != 0)
        {
            throw WindowsOperationErrors.Record(
                new NotSupportedException("owner dataのLISTVIEWは未対応です。"));
        }

        bool headers = column == -1;
        nint header = Native.SendMessageW(target, 0x101f, 0, 0);
        nint columns = header == 0 ? 0
            : Native.SendMessageW(header, 0x1200, 0, 0);
        nint count = headers ? columns
            : Native.SendMessageW(target, 0x1004, 0, 0);
        if ((selected || (enumeration && !headers)) && count < 0)
        {
            if (enumeration)
            {
                // GETITEMの行数は原版と同じchecked減算を行います。
                _ = WindowsOperationErrors.Subtract(WindowsOperationErrors.Int32(count), 1);
            }

            return [];
        }

        if (count < 0 || count > UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("LISTVIEWの項目数が範囲外です。"));
        }

        int subItem = Math.Max(0, column - 1);
        List<string> result = [];
        if (!headers && subItem > 0 && subItem >= columns)
        {
            return result;
        }

        using var buffer = new NativeControlTextBuffer(target,
            headers ? ControlTextKind.Column : ControlTextKind.ListView);
        int characters = 0;
        for (int index = 0; index < (int)count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (selected && Native.SendMessageW(target, 0x102c, index, 2) == 0)
            {
                continue;
            }

            buffer.Prepare(index, subItem);
            nint copied = Native.SendMessageW(target,
                headers ? 0x105fu : 0x1073u, index, buffer.ItemAddress);
            token.ThrowIfCancellationRequested();
            if (!Native.IsWindow(target) || (headers && copied == 0))
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("LISTVIEWの文字列取得に失敗しました。"));
            }

            string text = NativeListControl.Trim(buffer.Read());
            int size = text.Length + (selected && characters > 0 ? 1 : 0);
            if (size > NativeListControl.MaximumCharacters - characters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("LISTVIEWの文字列取得上限を超えました。"));
            }

            characters += size;
            result.Add(text);
            onItem?.Invoke(text);
        }

        return result;
    }
}
