using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

[SupportedOSPlatform("windows")]
internal static partial class AccessibleItems
{
    internal static Task<bool> InvokeAsync(nint root, string name,
        int flags, int occurrence, int request, CancellationToken cancellationToken,
        AccessibleActionContinuation? continuation = null, ProviderItemMouse? mouse = null) =>
        ProviderActionOperation.RunAsync(root, token =>
        {
            token.ThrowIfCancellationRequested();
            if ((flags & 512) == 0)
            {
                ItemActionPreparation.Foreground(root, token);
            }

            AccessibleActionContinuation.Snapshot? snapshot = continuation?.Begin(root, flags);
            bool actionSucceeded = true;
            List<string> result = Read(root, flags, (flags & 65536) != 0,
                true, 1, token, 7, Math.Max(1, occurrence), name,
                write: (target, child, current) => actionSucceeded = PerformRequestedAction(
                    root, target, child, current == "1", request,
                    (ClickItemFlags)flags, token, mouse),
                automaticAction: (flags & (int)ClickItemFlags.Accessible) == 0,
                resumePath: snapshot?.Path,
                selectedPath: continuation is null ? null
                    : path =>
                    {
                        if (actionSucceeded)
                        {
                            continuation.Save(snapshot!.Value, path);
                        }
                    });
            return actionSucceeded && result.Count != 0 && result[0] == "1";
        }, cancellationToken);

    internal static bool PerformRequestedAction(nint root, IAccessibleNative target,
        int child, bool current, int request, ClickItemFlags flags,
        CancellationToken token, ProviderItemMouse? mouse = null)
    {
        if ((flags & ProviderItemMouse.Options) == 0)
        {
            PerformAction(target, child, current, request, token);
            return true;
        }

        Focus(target, child, token);
        using var dpi = new PhysicalDpiScope();
        AccessibleReader.Location(target, out int x, out int y,
            out int width, out int height, child);
        mouse ??= new ProviderItemMouse(input => NativeInputSender.Send(input),
            Native.GetSystemMetrics(23) != 0, new NativeListSelectionPlatform());
        Native.Point? point = mouse.Move(x, y, width, height, flags, token);
        if (current == (request != 0))
        {
            return true;
        }

        if ((flags & ProviderItemMouse.Buttons) != 0)
        {
            if (!point.HasValue)
            {
                return false;
            }

            return mouse.Click(root, point.Value, flags, token);
        }
        else
        {
            WindowsOperationErrors.Provider(() => target.DoDefaultAction(child));
        }

        token.ThrowIfCancellationRequested();
        return true;
    }

    internal static void PerformAction(IAccessibleNative target, int child,
        bool current, int request, CancellationToken token)
    {
        Focus(target, child, token);
        if (current != (request != 0))
        {
            WindowsOperationErrors.Provider(() => target.DoDefaultAction(child));
            token.ThrowIfCancellationRequested();
        }
    }

    private static void Focus(IAccessibleNative target, int child,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // focus失敗後の物理操作への再試行は行いません。
        try
        {
            WindowsOperationErrors.Provider(() => target.Select(1, child));
        }
        catch (COMException failure) when (failure.HResult is
            unchecked((int)0x80020003) or unchecked((int)0x80004001))
        {
            // 任意のフォーカス操作が未提供でも、既定操作は独立して試みます。
        }
        token.ThrowIfCancellationRequested();
    }

    internal static Task<bool> WriteStringAsync(nint root,
        string text, int occurrence, int mode, bool cell,
        CancellationToken token) => Task.Run(() =>
    {
        List<string> result = Read(root, 0, false, occurrence < 0,
            1, token, cell ? 5 : 3, occurrence, write: (target, child, current) =>
            {
                string replacement = mode == 0 ? current + text : text;
                if (replacement.Length > NativeListControl.MaximumCharacters)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("送信文字数が上限を超えました。"));
                }

                token.ThrowIfCancellationRequested();
                WindowsOperationErrors.Provider(() =>
                    target.SetValue(child, replacement));
            });
        return result.Count != 0;
    }, token);

    internal static Task<List<string>> ReadAsync(nint root, int kinds,
        bool reverse, bool includeDisabled, int maximum,
        CancellationToken token, IAccessibleActivationBackend? activation = null, Action<string>? onItem = null)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            if ((kinds & 512) == 0)
            {
                AccessibleActivation.Prepare(root,
                    activation ?? new NativeAccessibleActivationBackend(), token);
            }

            return Read(root, kinds, reverse, includeDisabled, maximum, token, onItem: onItem);
        }, token);
    }

    internal static Task<string?> ReadStringAsync(nint root, int mode,
        int occurrence, CancellationToken token,
        Action<Native.Rect>? selected = null)
    {
        return Task.Run(() =>
        {
            List<string> result = Read(root, 0, false, true, 1,
                token, mode, occurrence, selected: selected);
            return result.Count == 0 || result[0] == "\u0001__EMPTY__"
                ? null : result[0];
        }, token);
    }

    internal static Task<int> ReadCheckAsync(nint root, string name,
        int occurrence, CancellationToken token)
    {
        return Task.Run(() =>
        {
            List<string> result = Read(root, 0, false, true, 1,
                token, 6, Math.Max(1, occurrence), name);
            return result.Count == 0 ? -1 : result[0][0] - '0';
        }, token);
    }

    internal static List<string> ReadProvider(IAccessibleNative accessible,
        int kinds, bool reverse, bool includeDisabled, int maximum,
        CancellationToken token, int stringMode = -1, int occurrence = 0,
        string desiredName = "", Action<string>? onItem = null,
        Action<IAccessibleNative, int, string>? write = null,
        Action<Native.Rect>? selected = null, bool automaticAction = false,
        int[]? resumePath = null, Action<int[]>? selectedPath = null)
    {
        List<string> result = [];
        var traversal = new Traversal(kinds, reverse, includeDisabled,
            maximum, token, result, stringMode, occurrence, desiredName, onItem, write, selected,
            automaticAction, resumePath, selectedPath);
        traversal.Visit(accessible, 0);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static List<string> Read(nint root, int kinds, bool reverse,
        bool includeDisabled, int maximum, CancellationToken token,
        int stringMode = -1, int occurrence = 0, string desiredName = "", Action<string>? onItem = null,
        Action<IAccessibleNative, int, string>? write = null,
        Action<Native.Rect>? selected = null, bool automaticAction = false,
        int[]? resumePath = null, Action<int[]>? selectedPath = null)
    {
        List<string> result = [];
        if (root == 0 || !Native.IsWindow(root))
        {
            return result;
        }

        IAccessibleNative? accessible = null;
        Exception? cleanupPrimary1 = null;
        try
        {
            token.ThrowIfCancellationRequested();
            Guid iid = typeof(IAccessibleNative).GUID;
            int status = AccessibleReader.AccessibleObjectFromWindow(
                root, 0xfffffffcu, ref iid, out accessible);
            if (status == unchecked((int)0x80004002))
            {
                return result;
            }

            WindowsOperationErrors.ThrowIfFailed(status);
            if (accessible is not null)
            {
                result = ReadProvider(accessible, kinds, reverse,
                    includeDisabled, maximum, token, stringMode, occurrence, desiredName, onItem, write, selected,
                    automaticAction, resumePath, selectedPath);
            }

            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception failure1)
        {
            cleanupPrimary1 = failure1;
            throw;
        }
        finally
        {
            var cleanup1 = new ProviderCleanup(cleanupPrimary1);
            cleanup1.ReleaseComOnly(accessible);
            cleanup1.Finish();
        }
    }

    private sealed class Traversal(int kinds, bool reverse, bool includeDisabled,
        int maximum, CancellationToken token, List<string> result,
        int stringMode, int occurrence, string desiredName, Action<string>? onItem,
        Action<IAccessibleNative, int, string>? write,
        Action<Native.Rect>? selected, bool automaticAction,
        int[]? resumePath, Action<int[]>? selectedPath)
    {
        private readonly record struct ActionContainer(int Role, NativeSelectionKind Kind);

        private readonly HashSet<nint> ancestors = [];
        private readonly List<ActionContainer> actionParents = [];
        private readonly List<int> path = [];
        private int visited;
        private int enumerated;
        private int characters;
        private int inspectedCharacters;
        private long stringMatches;
        private readonly HashSet<(int X, int Y, int Width, int Height, string Text)> seen = [];

        private bool Full => maximum > 0 && result.Count >= maximum;

        internal void Visit(IAccessibleNative accessible, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (Full)
            {
                return;
            }

            if (depth >= 128)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("MSAAの探索深度上限を超えました。"));
            }

            nint identity = Marshal.GetIUnknownForObject(accessible);
            Exception? cleanupPrimary2 = null;
            try
            {
                // 再marshalでidentityが変わるproviderも、深さ上限で保護します。
                if (!ancestors.Add(identity))
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("MSAAの親子関係が循環しています。"));
                }

                try
                {
                    if (automaticAction
                        && Integer(WindowsOperationErrors.Provider(
                            () => accessible.State(0))) is int state
                        && (state & 0x18001) != 0)
                    {
                        return;
                    }

                    Match(accessible, 0);
                    if (!Full)
                    {
                        if (automaticAction)
                        {
                            int role = Integer(WindowsOperationErrors.Provider(
                                () => accessible.Role(0))) ?? 0;
                            NativeSelectionKind kind = role is 33 or 46
                                ? NativeKind(accessible) : NativeSelectionKind.None;
                            actionParents.Add(new ActionContainer(role, kind));
                            try
                            {
                                Children(accessible, depth);
                            }
                            finally
                            {
                                actionParents.RemoveAt(actionParents.Count - 1);
                            }
                        }
                        else
                        {
                            Children(accessible, depth);
                        }
                    }
                }
                finally
                {
                    ancestors.Remove(identity);
                }
            }
            catch (Exception failure2) when (
                ProviderCleanup.PreservePrimary(failure2, ref cleanupPrimary2))
            {
                throw;
            }
            finally
            {
                var cleanup2 = new ProviderCleanup(cleanupPrimary2);
                cleanup2.ReleasePointer(identity);
                cleanup2.Finish();
            }
        }

        private void Match(IAccessibleNative accessible, int child)
        {
            token.ThrowIfCancellationRequested();
            if (BeforeResume(includeAncestors: true))
            {
                return;
            }

            if (++visited > 100000)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("MSAAの探索件数上限を超えました。"));
            }

            if (Integer(WindowsOperationErrors.Provider(() => accessible.Role(child))) is not int role
                || Integer(WindowsOperationErrors.Provider(() => accessible.State(child))) is not int state)
            {
                return;
            }

            if (stringMode == 6)
            {
                MatchCheck(accessible, child, role, state);
                return;
            }

            if (stringMode == 7)
            {
                MatchAction(accessible, child, role, state);
                return;
            }

            if (stringMode >= 3)
            {
                MatchString(accessible, child, role, state);
                return;
            }

            bool edits = (kinds & 16777216) != 0;
            bool clicks = (kinds & 4194304) != 0;
            bool texts = (kinds & 8388608) != 0;
            bool clickable = Clickable(role, state);
            if (clicks && (kinds & 268435456) != 0 && role == 42
                && (state & 0x200000) != 0)
            {
                clickable |= NativeListControl.Trim(WindowsOperationErrors.Provider(() => accessible.Name(child)) ?? "").Length != 0;
            }
            if (edits ? role != 42 || (state & 0x40) != 0
                : !((clicks && clickable)
                    || (texts && (role is >= 24 and <= 30 or 41 or 42))))
            {
                return;
            }

            AccessibleReader.Location(accessible, out int x, out int y,
                out int width, out int height, child);
            string text;
            if (edits)
            {
                if (width == 0 || (!includeDisabled && (state & 1) != 0))
                {
                    return;
                }

                // 編集欄のValueは空文字や前後空白を含め、そのまま保持します。
                text = WindowsOperationErrors.Provider(() => accessible.Value(child)) ?? "";
            }
            else
            {
                text = WindowsOperationErrors.Provider(() => accessible.Name(child)) ?? "";
                if (text.Length == 0 || role is 29 or 46 or 33 or 41 or 42 or 52)
                {
                    if (role != 42 || text.Length == 0)
                    {
                        string? value = WindowsOperationErrors.Provider(
                            () => accessible.Value(child));
                        if (value is not null && (role != 41 || value.Length != 0))
                        {
                            text = value;
                        }
                    }
                }

                text = NativeListControl.Trim(text.Replace('\u4383', ' '));
                if (text.Length > NativeListControl.MaximumCharacters - inspectedCharacters)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("MSAAの判定文字数上限を超えました。"));
                }

                inspectedCharacters += text.Length;
                // 共通matcherは出力候補の判定前に矩形と文字列の重複を除外します。
                if (seen.Contains((x, y, width, height, text)))
                {
                    return;
                }

                if (text.Length == 0 && role == 43)
                {
                    text = WindowsOperationErrors.Provider(
                        () => accessible.Description(child)) ?? "";
                }

                if (text.Length == 0 && role is not (43 or 44 or 45))
                {
                    seen.Add((x, y, width, height, text));
                    return;
                }

                if (clickable)
                {
                    if (!clicks)
                    {
                        seen.Add((x, y, width, height, text));
                        return;
                    }

                    if (!includeDisabled && (state & 1) != 0)
                    {
                        return;
                    }

                    text = ButtonName.Normalize(text);
                    if (text.Length == 0)
                    {
                        return;
                    }
                }
                else
                {
                    seen.Add((x, y, width, height, text));
                    if (!texts || !(role is >= 24 and <= 30 or 41
                        || (role == 42 && (state & 0x40) != 0)))
                    {
                        return;
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            if (text.Length > NativeListControl.MaximumCharacters - characters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("GETITEMの取得文字数上限を超えました。"));
            }

            characters += text.Length;
            result.Add(text);
            onItem?.Invoke(text);
        }

        private void MatchString(IAccessibleNative accessible, int child,
            int role, int state)
        {
            bool candidate = stringMode == 3 ? role == 42
                : stringMode == 5 ? role == 29
                : role is >= 24 and <= 30 or 41 or 42;
            if (!candidate)
            {
                return;
            }

            if (write is not null && !includeDisabled && (state & 1) != 0)
            {
                return;
            }

            AccessibleReader.Location(accessible, out int x, out int y,
                out int width, out int height, child);
            string text = ReadText(accessible, child, role, stringMode != 4);
            if (stringMode == 4)
            {
                text = NativeListControl.Trim(text.Replace('\u4383', ' '));
            }

            token.ThrowIfCancellationRequested();
            if (text.Length > NativeListControl.MaximumCharacters - inspectedCharacters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("MSAAの判定文字数上限を超えました。"));
            }

            inspectedCharacters += text.Length;
            if (stringMode == 4 ? role == 42 && (state & 0x40) == 0
                : (state & 0x40) != 0 || (stringMode == 3 && width == 0))
            {
                return;
            }

            long number = Math.Abs((long)occurrence);
            if (stringMode == 4)
            {
                number = Math.Max(1, number);
            }

            if (number == 0 ? (state & 4) == 0 : ++stringMatches != number)
            {
                return;
            }

            selected?.Invoke(new Native.Rect
            {
                Left = x,
                Top = y,
                Right = WindowsOperationErrors.Add(x, width),
                Bottom = WindowsOperationErrors.Add(y, height)
            });
            write?.Invoke(accessible, child, text);
            // 文字列取得・送信はGETITEMのButton正規化や重複cache更新を通りません。
            result.Add(text);
            onItem?.Invoke(text);
        }

        private void MatchAction(IAccessibleNative accessible, int child,
            int role, int state)
        {
            ClickItemFlags compatible = automaticAction
                ? AutomaticKinds(accessible, role) : 0;
            ClickItemFlags requested = (ClickItemFlags)kinds & ClickItemFlags.Types;
            if (requested == 0)
            {
                requested = ClickItemFlags.Types;
            }
            if (automaticAction && (compatible & requested) == 0)
            {
                return;
            }

            bool extra = (kinds & 268435456) != 0 && role == 42
                && (state & 0x200000) != 0
                && NativeListControl.Trim(WindowsOperationErrors.Provider(
                    () => accessible.Name(child)) ?? "").Length != 0;
            if (!Clickable(role, state) && !extra && compatible == 0)
            {
                return;
            }

            AccessibleReader.Location(accessible, out int x, out int y,
                out int width, out int height, child);
            string text = NativeListControl.Trim(ReadText(accessible, child, role, false,
                allowMissingValue: true)
                .Replace('\u4383', ' '));
            if (text.Length > NativeListControl.MaximumCharacters - inspectedCharacters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("MSAA操作の判定文字数上限を超えました。"));
            }

            inspectedCharacters += text.Length;
            if (!seen.Add((x, y, width, height, text)))
            {
                return;
            }

            if (text.Length == 0 && role == 43)
            {
                try
                {
                    text = WindowsOperationErrors.Provider(() => accessible.Description(child)) ?? "";
                }
                catch (COMException failure) when (failure.HResult is
                    unchecked((int)0x80020003) or unchecked((int)0x80004001))
                {
                    // CLKITEMの補助説明が未提供なら、この候補の名前は空のままです。
                }
            }

            bool partial = (kinds & 256) != 0;
            string desired = partial ? desiredName : NativeListControl.Trim(desiredName);
            bool match = partial
                ? ButtonName.Normalize(text).Contains(ButtonName.Normalize(desired),
                    StringComparison.OrdinalIgnoreCase)
                : MatchesName(text, desired);
            if ((text.Length == 0 && role is not (43 or 44 or 45))
                || !match || ++stringMatches != occurrence)
            {
                return;
            }

            // MSAAでは無効候補も番号を消費し、その候補で探索を終了します。
            if ((state & 0x18001) != 0)
            {
                result.Add("0");
                return;
            }

            bool on = (state & (role == 44 ? 0x10 : 0x1a)) != 0;
            token.ThrowIfCancellationRequested();
            write?.Invoke(accessible, child, on ? "1" : "0");
            token.ThrowIfCancellationRequested();
            selectedPath?.Invoke([.. path]);
            result.Add("1");
        }

        private bool BeforeResume(bool includeAncestors)
        {
            if (resumePath is null)
            {
                return false;
            }

            int length = Math.Min(path.Count, resumePath.Length);
            for (int index = 0; index < length; index++)
            {
                if (path[index] != resumePath[index])
                {
                    return reverse ? path[index] > resumePath[index]
                        : path[index] < resumePath[index];
                }
            }

            return includeAncestors && path.Count < resumePath.Length;
        }

        private ClickItemFlags AutomaticKinds(IAccessibleNative accessible, int role)
        {
            ClickItemFlags container = 0;
            for (int index = actionParents.Count - 1; index >= 0; index--)
            {
                ActionContainer parent = actionParents[index];
                if (parent.Role == 22)
                {
                    container = ClickItemFlags.Toolbar;
                    break;
                }
                if (parent.Role is 24 or 28 || parent.Kind == NativeSelectionKind.ListView)
                {
                    container = ClickItemFlags.ListView;
                    break;
                }
                if (parent.Role is 33 or 46)
                {
                    container = ClickItemFlags.List;
                    break;
                }
            }

            return role switch
            {
                43 or 56 or 57 or 58 or 62 => ClickItemFlags.Button
                    | (container == ClickItemFlags.Toolbar ? ClickItemFlags.Toolbar : 0),
                44 or 45 => ClickItemFlags.Button | container,
                12 => ClickItemFlags.Menu,
                36 => ClickItemFlags.TreeView,
                37 => ClickItemFlags.Tab,
                34 => container is ClickItemFlags.List or ClickItemFlags.ListView
                    ? container : NativeKind(accessible) == NativeSelectionKind.ListView
                        ? ClickItemFlags.ListView : ClickItemFlags.List,
                28 or 29 when container == ClickItemFlags.ListView => ClickItemFlags.ListView,
                _ => 0
            };
        }

        private static NativeSelectionKind NativeKind(IAccessibleNative accessible)
        {
            // HWNDが未提供のproviderではroleの情報だけで分類します。
            return WindowFromAccessibleObject(accessible, out nint window) == 0
                && window != 0 && Native.IsWindow(window)
                ? NativeSelectionTarget.Kind(NativeSelectionTarget.Label(window))
                : NativeSelectionKind.None;
        }

        private void MatchCheck(IAccessibleNative accessible, int child,
            int role, int state)
        {
            if (!Clickable(role, state))
            {
                return;
            }

            AccessibleReader.Location(accessible, out int x, out int y,
                out int width, out int height, child);
            string text = ReadText(accessible, child, role, false);
            token.ThrowIfCancellationRequested();
            text = NativeListControl.Trim(text.Replace('\u4383', ' '));
            if (text.Length > NativeListControl.MaximumCharacters - inspectedCharacters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("MSAAの判定文字数上限を超えました。"));
            }

            inspectedCharacters += text.Length;
            if (seen.Contains((x, y, width, height, text)))
            {
                return;
            }

            if (text.Length == 0 && role == 43)
            {
                text = WindowsOperationErrors.Provider(() => accessible.Description(child)) ?? "";
            }

            token.ThrowIfCancellationRequested();
            if ((text.Length == 0 && role is not (43 or 44 or 45))
                || !MatchesName(text, desiredName) || ++stringMatches != occurrence)
            {
                seen.Add((x, y, width, height, text));
                return;
            }

            token.ThrowIfCancellationRequested();
            bool on = (state & (role == 44 ? 0x10 : 0x1a)) != 0;
            result.Add((state & 0x20) != 0 ? "2" : on ? "1" : "0");
        }

        private void Children(IAccessibleNative accessible, int depth)
        {
            token.ThrowIfCancellationRequested();
            int count = WindowsOperationErrors.Provider(() => accessible.ChildCount());
            if (count < 0 || count > 100000 - enumerated)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("MSAAの子要素件数上限を超えました。"));
            }

            enumerated += count;
            if (count == 0)
            {
                return;
            }

            // providerの列挙位置を二重に進めないよう、子VARIANTを一括取得します。
            int size = IntPtr.Size == 8 ? 24 : 16;
            token.ThrowIfCancellationRequested();
            int length = count;
            int start = 0;
            byte[] initialized = new byte[size * length];
            List<object?> children = [];
            nint buffer = Marshal.AllocCoTaskMem(initialized.Length);
            Exception? cleanupPrimary3 = null;
            try
            {
                Marshal.Copy(initialized, 0, buffer, initialized.Length);
                int status = AccessibleChildren(accessible, start, length,
                    buffer, out int obtained);
                WindowsOperationErrors.ThrowIfFailed(status);
                if (obtained < 0 || obtained > length)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("MSAAの列挙件数が不正です。"));
                }

                for (int i = 0; i < obtained; i++)
                {
                    token.ThrowIfCancellationRequested();
                    children.Add(Marshal.GetObjectForNativeVariant(buffer + i * size));
                }

                for (int i = 0; i < obtained && !Full; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int position = reverse ? obtained - 1 - i : i;
                    object? child = children[position];
                    path.Add(position);
                    try
                    {
                        if (BeforeResume(includeAncestors: false))
                        {
                            continue;
                        }

                        if (child is IAccessibleNative element)
                        {
                            Visit(element, depth + 1);
                        }
                        else if (child is int childId && childId > 0)
                        {
                            VisitChild(accessible, childId, depth);
                        }
                        else
                        {
                            throw WindowsOperationErrors.Record(
                                new NotSupportedException("MSAAの子VARIANT形式が未対応です。"));
                        }
                    }
                    finally
                    {
                        path.RemoveAt(path.Count - 1);
                    }
                }
            }
            catch (Exception failure3) when (
                ProviderCleanup.PreservePrimary(failure3, ref cleanupPrimary3))
            {
                throw;
            }
            finally
            {
                var cleanup3 = new ProviderCleanup(cleanupPrimary3);
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    cleanup3.ReleaseComOnly(children[index]);
                }

                for (int i = length - 1; i >= 0; i--)
                {
                    cleanup3.ClearVariant(buffer + i * size);
                }

                cleanup3.Free(buffer);
                cleanup3.Finish();
            }
        }

        private void VisitChild(IAccessibleNative accessible, int childId, int depth)
        {
            object? child = null;
            Exception? cleanupPrimary4 = null;
            try
            {
                try
                {
                    child = WindowsOperationErrors.Provider(() => accessible.Child(childId));
                }
                catch (COMException error) when (error.HResult is
                    unchecked((int)0x80004002) or unchecked((int)0x80004001))
                {
                    // 単純子要素は親のIAccessibleと子IDで取得します。
                }

                if (child is IAccessibleNative element)
                {
                    Visit(element, depth + 1);
                }
                else if (child is null)
                {
                    Match(accessible, childId);
                }
                else
                {
                    throw WindowsOperationErrors.Record(
                        new NotSupportedException("MSAAの子interfaceが未対応です。"));
                }
            }
            catch (Exception failure4)
            {
                cleanupPrimary4 = failure4;
                throw;
            }
            finally
            {
                var cleanup4 = new ProviderCleanup(cleanupPrimary4);
                cleanup4.ReleaseComOnly(child);
                cleanup4.Finish();
            }
        }
    }

    internal static bool Clickable(int role, int state)
    {
        return role is 12 or 30 or 34 or 36 or 37 or 43 or 51 or 52
            or 56 or 57 or 58 or 62 or 45 or 44
            || (role is 40 or 42 && (state & 0x400000) != 0)
            || (role == 10 && (state & 0x300000) != 0 && (state & 0x40) == 0);
    }

    private static int? Integer(object? value)
    {
        Exception? cleanupPrimary5 = null;
        try
        {
            return value is int number ? number : null;
        }
        catch (Exception failure5)
        {
            cleanupPrimary5 = failure5;
            throw;
        }
        finally
        {
            var cleanup5 = new ProviderCleanup(cleanupPrimary5);
            // 不正なRole/State VARIANTがinterfaceでも、取得参照を解放します。
            cleanup5.ReleaseComOnly(value);
            cleanup5.Finish();
        }
    }

    private static string ReadText(IAccessibleNative accessible, int child,
        int role, bool preferValue, bool allowMissingValue = false)
    {
        string text = WindowsOperationErrors.Provider(() => accessible.Name(child)) ?? "";
        if (preferValue || text.Length == 0 || role is 29 or 46 or 33 or 41 or 42 or 52)
        {
            if (preferValue || role != 42 || text.Length == 0)
            {
                string? value = null;
                try
                {
                    value = WindowsOperationErrors.Provider(() => accessible.Value(child));
                }
                catch (COMException failure) when (allowMissingValue && failure.HResult is
                    unchecked((int)0x80020003) or unchecked((int)0x80004001))
                {
                    // CLKITEMの名前照合では未提供のValueを空の補助値として扱います。
                }
                if (value is not null && (role != 41 || value.Length != 0))
                {
                    text = value;
                }
            }
        }

        return text;
    }

    internal static bool MatchesName(string candidate, string desired)
    {
        if (candidate.Length > NativeListControl.MaximumCharacters
            || desired.Length > NativeListControl.MaximumCharacters)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("MSAAの名前文字数上限を超えました。"));
        }

        candidate = ButtonName.Normalize(candidate).ToUpperInvariant();
        desired = desired.ToUpperInvariant();
        if (candidate == desired || NativeListControl.Trim(candidate) == desired)
        {
            return true;
        }

        string replaced = candidate.Replace('\uf8f0', ' ');
        if (replaced == desired || NativeListControl.Trim(replaced) == desired)
        {
            return true;
        }

        candidate = HalfWidth(candidate);
        desired = HalfWidth(desired);
        return candidate == desired || candidate.Replace('\uf8f0', ' ') == desired;
    }

    private static string HalfWidth(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        int length = Native.LCMapStringW(0x800, 0x400000, text, text.Length, null, 0);
        if (length == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (length > NativeListControl.MaximumCharacters)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("MSAAの名前変換上限を超えました。"));
        }

        // 正の入力長でのAPI出力は終端保証がないため、照会長だけを文字列化します。
        char[] result = new char[length];
        if (Native.LCMapStringW(0x800, 0x400000, text, text.Length, result, length) == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return new string(result);
    }
}
