using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal static class NativeTreeControl
{
    private sealed record Entry(nint Handle, string Name, string Path, uint MatchFlags = 0, int MatchNumber = 0);

    internal static Task<(bool Found, bool Success)> ClickAsync(nint target,
        string label, bool on, CancellationToken token)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Click(target, label, on, token));
        }

        return NativeControlWorker.RunAsync(active => Click(target, label, on, active), token);
    }

    private static bool IsCheckedImage(int image) =>
        image != 0 && ((image - 1) & 1) != 0;

    internal static (bool Found, bool Success) Click(nint target,
        string label, bool on, CancellationToken token,
        NativeTreeClickBuffer? shared = null, int kind = 16,
        IListSelectionPlatform? platform = null, Action<int>? readCheck = null,
        int number = 0, Action? onAttempt = null)
    {
        if (shared is not null)
        {
            return ClickCore(target, label, on, token, shared, kind,
                platform ?? new NativeListSelectionPlatform(), readCheck, number, onAttempt);
        }

        using var owner = new NativeControlBufferOwner(Native.GetAncestor(target, 2));
        return ClickCore(target, label, on, token, new NativeTreeClickBuffer(owner),
            kind, platform ?? new NativeListSelectionPlatform(), readCheck, number, onAttempt);
    }

    private static (bool Found, bool Success) ClickCore(nint target,
        string label, bool on, CancellationToken token, NativeTreeClickBuffer shared,
        int kind, IListSelectionPlatform platform, Action<int>? readCheck, int number,
        Action? onAttempt)
    {
        token.ThrowIfCancellationRequested();

        bool partial = (kind & 256) != 0;
        string desired = NativeListNameMatcher.AsciiUpper(
            partial ? label : NativeListControl.Trim(label));
        string[] segments = NativeTreePath.Split(desired);
        bool rooted = desired.StartsWith('\\');
        if (segments.Length <= 1)
        {
            // 単一名枝は分割結果ではなく、入口の名前から先頭1文字だけを除きます。
            segments = [rooted ? desired[1..] : desired];
        }
        nint first = Next(target, 0, 0, token);
        Entry? entry = Find(first, segments[0], segments.Length == 1 && !rooted, segments.Length > 1 ? -1 : 0);
        if (entry is null && segments.Length > 1 && !rooted)
        {
            entry = Find(first, segments[0], true, -1);
        }

        if (entry is null)
        {
            return (false, false);
        }

        for (int index = 0; index < segments.Length; index++)
        {
            if (index != 0)
            {
                entry = Find(Next(target, 4, entry.Handle, token), segments[index], false);
                if (entry is null)
                {
                    // 後続要素が不一致でもcallerはNULL項目を選択します。
                    _ = NativeTreeSelection.Select(target, 0, platform, token);
                    return (false, false);
                }
            }

            onAttempt?.Invoke();
            _ = NativeTreeSelection.SelectMatch(shared.Root, target,
                entry.Handle, entry.MatchFlags, (kind & 512) != 0, platform, token,
                callerSelection: index != 0);
        }

        token.ThrowIfCancellationRequested();
        int image = shared.ReadStateImage(target, entry.Handle, token);
        if (readCheck is not null)
        {
            // 原版の照会は選択後に状態を返し、Spaceや座標操作を行いません。
            readCheck(IsCheckedImage(image) ? 1 : 0);
            return (true, true);
        }

        if ((kind & 0x3c00) != 0)
        {
            Native.Rect rectangle = shared.ReadRectangle(target, entry.Handle, token);
            var point = new Native.Point(
                WindowsOperationErrors.Add(rectangle.Left, 4),
                WindowsOperationErrors.Add(rectangle.Top, 4));
            Native.Point screen = platform.ToScreen(target, point);
            token.ThrowIfCancellationRequested();
            platform.MoveCursor(screen);
            token.ThrowIfCancellationRequested();
            if ((kind & 0x3800) != 0)
            {
                NativeControlMouse.Post(target, kind, point, platform, token);
                return (true, true);
            }
        }

        if (image != 0 && IsCheckedImage(image) != on)
        {
            var keys = new NativeListSelectionBackend(shared.Root, target,
                combo: false, back: (kind & 512) != 0, platform: platform);
            keys.PressKey(32, token);
        }

        // 原版は一致済み項目の状態を再確認せず、要求後に成功退出します。
        return (true, true);

        Entry? Find(nint start, string name, bool descend, int initial = 0)
        {
            bool Matches(string candidate)
            {
                string text = NativeListNameMatcher.AsciiUpper(candidate);
                return NativeListNameMatcher.Compare(ref text, name, partial);
            }

            foreach (Entry candidate in ReadClickEntries(target, shared, token,
                start, descend, Matches, initial))
            {
                if (initial < 0 || number <= 0 || candidate.MatchNumber == number)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    internal static Task<(bool Found, int Value)> CheckAsync(nint target,
        string label, CancellationToken token)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Check(target, label, token));
        }

        return NativeControlWorker.RunAsync(active => Check(target, label, active), token);
    }

    internal static (bool Found, int Value) Check(nint target, string label,
        CancellationToken token, NativeTreeClickBuffer? shared = null,
        IListSelectionPlatform? platform = null, int number = 0)
    {
        int value = -1;
        (bool found, bool success) = Click(target, label, false, token, shared,
            platform: platform, readCheck: state => value = state, number: number);
        return (found, success ? value : -1);
    }

    internal static Task<string> SelectedAsync(nint target, CancellationToken token)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Selected(target, token));
        }

        return NativeControlWorker.RunAsync(active => Selected(target, active), token);
    }

    private static string Selected(nint target, CancellationToken token)
    {
        using var buffer = new NativeControlTextBuffer(target, ControlTextKind.Tree);
        nint item = Next(target, 9, 0, token);
        string path = "";
        var visited = new HashSet<nint>();
        while (item != 0)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(item) || visited.Count > 257)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TREEVIEWの親探索上限または循環を検出しました。"));
            }

            buffer.Prepare(item);
            if (Native.SendMessageW(target, 0x113e, 0, buffer.ItemAddress) == 0)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TREEVIEWの選択文字列取得に失敗しました。"));
            }

            token.ThrowIfCancellationRequested();
            string name = buffer.Read();
            int size = name.Length + (path.Length == 0 ? 0 : 1);
            if (size > NativeListControl.MaximumCharacters - path.Length)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TREEVIEWの選択文字列取得上限を超えました。"));
            }

            // 選択文字列は原版の採用範囲に合わせ、trimせず親から子へ並べます。
            path = path.Length == 0 ? name : name + "\\" + path;
            item = Next(target, 3, item, token);
        }

        return path;
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

    private static nint Next(nint target, int kind, nint item,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint result = Native.SendMessageW(target, 0x110a, kind, item);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static List<string> Read(nint target, CancellationToken token,
        Action<string>? onItem = null) =>
        [.. ReadEntries(target, token, onItem).Select(entry => entry.Path)];

    private static IEnumerable<Entry> ReadClickEntries(nint target,
        NativeTreeClickBuffer shared, CancellationToken token, nint first, bool descend,
        Func<string, bool> matches, int initial)
    {
        shared.Begin();
        var visited = new HashSet<nint>();
        int characters = 0;
        return Branch(first, "", 0, initial);

        IEnumerable<Entry> Branch(nint first, string parent, int depth, int count)
        {
            for (nint item = first; item != 0; item = Next(target, 1, item, token))
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(item) || depth > 256
                    || visited.Count > UwscArray.MaximumElements)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("TREEVIEWの探索上限または循環を検出しました。"));
                }

                (string name, int children) = shared.ReadItem(target, item, token);
                string path = parent.Length == 0 ? name : parent + "\\" + name;
                if (path.Length > NativeListControl.MaximumCharacters - characters)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("TREEVIEWの文字列取得上限を超えました。"));
                }

                characters += path.Length;
                if (matches(name))
                {
                    if (count >= 0)
                    {
                        count = WindowsOperationErrors.Add(count, 1);
                    }

                    yield return new(item, name, path, shared.MatchFlags, count);
                }
                if (descend && children != 0)
                {
                    foreach (Entry child in Branch(Next(target, 4, item, token), path, depth + 1, count))
                    {
                        yield return child;
                    }
                }
            }
        }
    }

    private static List<Entry> ReadEntries(nint target, CancellationToken token,
        Action<string>? onItem = null)
    {
        using var buffer = new NativeControlTextBuffer(target, ControlTextKind.Tree);
        List<Entry> result = [];
        var visited = new HashSet<nint>();
        var pending = new Stack<(nint Item, string Parent, int Depth)>();
        nint root = Next(target, 0, 0, token);
        if (root != 0)
        {
            pending.Push((root, "", 0));
        }

        int characters = 0;
        while (pending.TryPop(out (nint Item, string Parent, int Depth) entry))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(entry.Item) || entry.Depth > 256
                || result.Count == UwscArray.MaximumElements)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TREEVIEWの探索上限または循環を検出しました。"));
            }

            buffer.Prepare(entry.Item);
            if (Native.SendMessageW(target, 0x113e, 0, buffer.ItemAddress) == 0)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TREEVIEWの文字列取得に失敗しました。"));
            }

            token.ThrowIfCancellationRequested();
            string name = NativeListControl.Trim(buffer.Read());
            string path = entry.Parent.Length == 0 ? name : entry.Parent + "\\" + name;
            if (path.Length > NativeListControl.MaximumCharacters - characters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("TREEVIEWの文字列取得上限を超えました。"));
            }

            result.Add(new(entry.Item, name, path));
            onItem?.Invoke(path);
            characters += path.Length;
            nint sibling = Next(target, 1, entry.Item, token);
            if (sibling != 0)
            {
                pending.Push((sibling, entry.Parent, entry.Depth));
            }

            nint child = Next(target, 4, entry.Item, token);
            if (child != 0)
            {
                pending.Push((child, path, entry.Depth + 1));
            }
        }

        return result;
    }
}
