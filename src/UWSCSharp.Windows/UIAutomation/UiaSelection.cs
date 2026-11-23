using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// UIAの選択containerを型別の候補順で取得します。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UiaSelection
{
    /// <summary>
    /// Mozilla系ウィンドウのクラス名の先頭部分です。
    /// </summary>
    private const string MozillaClassPrefix = "MOZILLA";

    /// <summary>
    /// 大文字小文字を区別せず、Mozilla系ウィンドウのクラス名か判定します。
    /// </summary>
    internal static bool IsMozilla(string className) =>
        className.StartsWith(MozillaClassPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool PrefersUia(string className) =>
        className.StartsWith("HWNDWRAPPER[WPFAPPLICATION", StringComparison.OrdinalIgnoreCase)
        || className.Equals("APPLICATIONFRAMEWINDOW", StringComparison.OrdinalIgnoreCase)
        || className.Equals("IEFRAME", StringComparison.OrdinalIgnoreCase)
        || className.StartsWith("CHROME_WIDGETWIN", StringComparison.OrdinalIgnoreCase)
        || IsMozilla(className);

    internal static Task<string> ReadAsync(nint root, int occurrence, int column,
        CancellationToken token, UiaMozillaPreparation? preparation = null) => Task.Run(() =>
            preparation is null ? Read(root, occurrence, column, token)
                : preparation.Execute(root, prepare => Read(root, occurrence, column, token, prepare), token), token);

    private static string Read(nint root, int occurrence, int column, CancellationToken token,
        Action? prepare = null)
    {
        token.ThrowIfCancellationRequested();
        if (root == 0 || !Native.IsWindow(root))
        {
            return "";
        }

        List<object> owned = [];
        Exception? primary = null;
        try
        {
            IUiaAutomation automation = WindowsOperationErrors.Provider(
                () => (IUiaAutomation)new UiaComObject());
            owned.Add(automation);
            IUiaElement element = WindowsOperationErrors.ProviderReference(
                () => automation.ElementFromHandle(root));
            owned.Add(element);
            if (prepare is not null)
            {
                prepare();
                element = WindowsOperationErrors.ProviderReference(() => automation.ElementFromHandle(root));
                owned.Add(element);
            }

            int remaining = Math.Max(1, occurrence);
            int candidates = 0;
            foreach (int type in new[] { 50008, 50003, 50023, 50028 })
            {
                token.ThrowIfCancellationRequested();
                IUiaCondition kind = WindowsOperationErrors.ProviderReference(
                    () => automation.CreatePropertyCondition(30003, type));
                owned.Add(kind);
                // 原版helperは有効状態を条件にせず、型別候補の番号を消費します。
                IUiaElements children = WindowsOperationErrors.ProviderReference(
                    () => element.FindAll(7, kind));
                owned.Add(children);
                int count = WindowsOperationErrors.Provider(() => children.Length());
                if (count < 0 || count > 100000 - candidates)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("UIA選択候補数が上限を超えました。"));
                }

                candidates += count;
                if (remaining > count)
                {
                    remaining -= count;
                    continue;
                }

                // DataGrid候補番号の確定後、要素取得前に列を解決します。
                int cellIndex = type == 50028
                    ? Math.Max(0, checked(column - 1)) : 0;
                IUiaElement target = WindowsOperationErrors.ProviderReference(
                    () => children.GetElement(remaining - 1));
                owned.Add(target);
                if (type is 50008 or 50003)
                {
                    if (type == 50008)
                    {
                        PrepareListScroll(target, owned, token);
                    }

                    if (type == 50003)
                    {
                        return ReadCombo(root, automation, target, owned, token);
                    }

                    return ReadListItems(automation, target, owned, token) ?? "";
                }

                Guid iid = typeof(IUiaSelectionPattern).GUID;
                object? pattern = WindowsOperationErrors.Provider(
                    () => target.PatternAs(10001, ref iid));
                if (pattern is not IUiaSelectionPattern selection)
                {
                    if (pattern is not null && Marshal.IsComObject(pattern))
                    {
                        owned.Add(pattern);
                    }

                    return "";
                }

                owned.Add(pattern);
                IUiaElements selected = WindowsOperationErrors.ProviderReference(
                    () => selection.GetCurrentSelection());
                owned.Add(selected);
                int selectedCount = WindowsOperationErrors.Provider(() => selected.Length());
                if (selectedCount < 0 || selectedCount > UWSCSharp.Core.UwscArray.MaximumElements)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("UIA選択要素数が上限を超えました。"));
                }

                List<string> result = [];
                int characters = 0;
                for (int index = 0; index < selectedCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    IUiaElement item = WindowsOperationErrors.ProviderReference(
                        () => selected.GetElement(index));
                    Exception? cleanupPrimary1 = null;
                    try
                    {
                        IUiaElement textTarget = item;
                        if (type == 50028)
                        {
                            // 行headerなどを列として数えず、GridItemを公開するcellを取得します。
                            IUiaCondition cellCondition = WindowsOperationErrors.ProviderReference(
                                () => automation.CreatePropertyCondition(30029, true));
                            owned.Add(cellCondition);
                            IUiaElements cells = WindowsOperationErrors.ProviderReference(
                                () => item.FindAll(2, cellCondition));
                            owned.Add(cells);
                            int cellCount = WindowsOperationErrors.Provider(() => cells.Length());
                            if (cellCount < 0 || cellCount > 100000)
                            {
                                throw WindowsOperationErrors.Record(
                                    new InvalidOperationException("UIA列数が上限を超えました。"));
                            }

                            if (cellIndex >= cellCount)
                            {
                                return "";
                            }

                            textTarget = WindowsOperationErrors.ProviderReference(
                                () => cells.GetElement(cellIndex));
                            owned.Add(textTarget);
                        }

                        string text = type == 50023
                            ? ReadTreePath(automation, item, token) : Name(textTarget);
                        int separator = result.Count == 0 ? 0 : 1;
                        if (text.Length > NativeListControl.MaximumCharacters - characters - separator)
                        {
                            throw WindowsOperationErrors.Record(
                                new InvalidOperationException("UIA選択文字数が上限を超えました。"));
                        }

                        characters += text.Length + separator;
                        result.Add(text);
                        if (type == 50028)
                        {
                            break;
                        }
                    }
                    catch (Exception failure1)
                    {
                        cleanupPrimary1 = failure1;
                        throw;
                    }
                    finally
                    {
                        var cleanup1 = new ProviderCleanup(cleanupPrimary1);
                        cleanup1.Release(item);
                        cleanup1.Finish();
                    }
                }

                token.ThrowIfCancellationRequested();
                return string.Join('\t', result);
            }

            return "";
        }
        catch (COMException error) when (error.HResult is
            unchecked((int)0x80040200) or unchecked((int)0x80040201)
            or unchecked((int)0x80040204) or unchecked((int)0x80004002))
        {
            token.ThrowIfCancellationRequested();
            return "";
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            ProviderCleanupOperations operations = ProviderCleanupOperations.Current;
            var errors = new List<Exception>();
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                try
                {
                    operations.Release(owned[index]);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }

            if (errors.Count > 0)
            {
                var cleanup = new AggregateException("UIA選択取得の解放に失敗しました。", errors);
                if (primary is not null)
                {
                    primary.Data["UWSCSharp.UiaSelectionCleanupErrors"] = cleanup;
                }
                else
                {
                    throw cleanup;
                }
            }
        }
    }

    private static string Name(IUiaElement item) => StringProperty(item, 30005);

    private static string ReadCombo(nint root,
        IUiaAutomation automation, IUiaElement target,
        List<object> owned, CancellationToken token)
    {
        if (WindowsOperationErrors.Provider(() => target.Property(30020)) is int handle
            && handle != 0)
        {
            nint window = unchecked((nint)(uint)handle);
            if ((window == root || Native.IsChild(root, window))
                && NativeSelectionTarget.Kind(NativeSelectionTarget.Label(window))
                    == NativeSelectionKind.Combo)
            {
                return NativeListControl.SelectedAsync(window, token)
                    .GetAwaiter().GetResult();
            }
        }

        string? selected = ReadComboSelection(automation, target, owned, token);
        if (selected is not null)
        {
            return selected;
        }

        object? pattern = null;
        try
        {
            Guid iid = typeof(IUiaExpandCollapsePattern).GUID;
            pattern = WindowsOperationErrors.Provider(() => target.PatternAs(10005, ref iid));
        }
        catch (COMException error) when (error.HResult is
            unchecked((int)0x80040204) or unchecked((int)0x80004002))
        {
            token.ThrowIfCancellationRequested();
        }

        if (pattern is not null)
        {
            owned.Add(pattern);
        }

        if (pattern is IUiaExpandCollapsePattern expandable)
        {
            return ReadExpanded(expandable,
                () => ReadListItems(automation, target, owned, token) ?? "", token);
        }

        string? text = ReadListItems(automation, target, owned, token);
        if (text is not null)
        {
            return text;
        }

        // 展開patternを提供しないComboに限り、従来の値取得を補完します。
        string value = StringProperty(target, 30045);
        text = value.Length == 0 ? StringProperty(target, 30093) : value;
        token.ThrowIfCancellationRequested();
        return text;
    }

    private static string ReadTreePath(IUiaAutomation automation,
        IUiaElement item, CancellationToken token)
    {
        List<object> owned = [];
        Exception? primary = null;
        try
        {
            IUiaTreeWalker walker = WindowsOperationErrors.ProviderReference(
                () => automation.RawViewWalker());
            owned.Add(walker);
            IUiaElement current = item;
            List<string> names = [];
            int characters = 0;
            for (int depth = 0; depth < 128; depth++)
            {
                token.ThrowIfCancellationRequested();
                object? type = WindowsOperationErrors.Provider(
                    () => current.Property(30003));
                if (type is 50023)
                {
                    names.Reverse();
                    return names.Count == 0 ? "" : "\\" + string.Join('\\', names);
                }

                if (type is 50024)
                {
                    string name = Name(current);
                    if (name.Length >= NativeListControl.MaximumCharacters - characters)
                    {
                        throw WindowsOperationErrors.Record(new InvalidOperationException(
                            "UIA Treeの選択文字数が上限を超えました。"));
                    }

                    characters += name.Length + 1;
                    names.Add(name);
                }

                IUiaElement? parent = WindowsOperationErrors.Provider(
                    () => walker.Parent(current));
                if (parent is null)
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException(
                        "UIA Treeの選択要素の親を取得できません。"));
                }

                owned.Add(parent);
                current = parent;
            }

            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "UIA Treeの祖先探索深度が上限を超えました。"));
        }
        catch (Exception failure) when (
            ProviderCleanup.PreservePrimary(failure, ref primary))
        {
            throw;
        }
        finally
        {
            var cleanup = new ProviderCleanup(primary);
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                cleanup.Release(owned[index]);
            }

            cleanup.Finish();
        }
    }

    private static string? ReadComboSelection(IUiaAutomation automation,
        IUiaElement target, List<object> owned, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        object? pattern;
        try
        {
            Guid iid = typeof(IUiaSelectionPattern).GUID;
            pattern = WindowsOperationErrors.Provider(
                () => target.PatternAs(10001, ref iid));
        }
        catch (COMException error) when (error.HResult is
            unchecked((int)0x80040204) or unchecked((int)0x80004002))
        {
            token.ThrowIfCancellationRequested();
            return null;
        }

        if (pattern is not null)
        {
            owned.Add(pattern);
        }

        if (pattern is not IUiaSelectionPattern selection)
        {
            return null;
        }

        // 選択を直接取得できる場合は、表示状態を変えません。
        IUiaElements items = WindowsOperationErrors.ProviderReference(
            () => selection.GetCurrentSelection());
        owned.Add(items);
        int count = WindowsOperationErrors.Provider(() => items.Length());
        if (count < 0 || count > UWSCSharp.Core.UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "UIA Comboの選択項目数が上限を超えました。"));
        }

        IUiaCondition textKind = WindowsOperationErrors.ProviderReference(
            () => automation.CreatePropertyCondition(30003, 50020));
        owned.Add(textKind);
        var result = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            IUiaElement item = WindowsOperationErrors.ProviderReference(
                () => items.GetElement(index));
            Exception? primary = null;
            try
            {
                string text = ReadItemName(item, textKind);
                int separator = result.Length == 0 ? 0 : 1;
                if (text.Length > NativeListControl.MaximumCharacters
                    - result.Length - separator)
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException(
                        "UIA Comboの選択文字数が上限を超えました。"));
                }

                if (separator != 0)
                {
                    result.Append('\t');
                }

                result.Append(text);
            }
            catch (Exception failure) when (
                ProviderCleanup.PreservePrimary(failure, ref primary))
            {
                throw;
            }
            finally
            {
                var cleanup = new ProviderCleanup(primary);
                cleanup.Release(item);
                cleanup.Finish();
            }
        }

        token.ThrowIfCancellationRequested();
        return result.ToString();
    }

    private static string ReadItemName(IUiaElement item,
        IUiaCondition textKind)
    {
        IUiaElement? textChild = WindowsOperationErrors.Provider(
            () => item.FindFirst(2, textKind));
        Exception? primary = null;
        try
        {
            return Name(textChild ?? item);
        }
        catch (Exception failure) when (
            ProviderCleanup.PreservePrimary(failure, ref primary))
        {
            throw;
        }
        finally
        {
            var cleanup = new ProviderCleanup(primary);
            if (textChild is not null)
            {
                cleanup.Release(textChild);
            }

            cleanup.Finish();
        }
    }

    internal static string ReadExpanded(IUiaExpandCollapsePattern pattern,
        Func<string> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Exception? primary = null;
        try
        {
            WindowsOperationErrors.Provider(() => pattern.Expand());
            token.ThrowIfCancellationRequested();
            string result = read();
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            // 展開途中の失敗や取消後もCollapseを試みます。
            try
            {
                WindowsOperationErrors.Provider(() => pattern.Collapse());
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["UWSCSharp.UiaComboCollapseError"] = cleanup;
            }
        }
    }

    private static void PrepareListScroll(IUiaElement target, List<object> owned,
        CancellationToken token)
    {
        object? pattern;
        try
        {
            Guid iid = typeof(IUiaScrollPattern).GUID;
            pattern = WindowsOperationErrors.Provider(() => target.PatternAs(10004, ref iid));
        }
        catch (COMException error) when (error.HResult is
            unchecked((int)0x80040204) or unchecked((int)0x80004002))
        {
            token.ThrowIfCancellationRequested();
            return;
        }

        if (pattern is not null)
        {
            owned.Add(pattern);
        }

        if (pattern is IUiaScrollPattern scroll)
        {
            PrepareScroll(scroll, token);
        }
    }

    internal static void PrepareScroll(IUiaScrollPattern scroll, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double saved = WindowsOperationErrors.Provider(() => scroll.VerticalScrollPercent());
        if (saved == -1)
        {
            return;
        }

        if (!double.IsFinite(saved) || saved < 0 || saved > 100)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("UIAの縦スクロール位置が範囲外です。"));
        }

        Exception? primary = null;
        bool attempted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            attempted = true;
            WindowsOperationErrors.Provider(() => scroll.SetScrollPercent(-1, 100));
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            primary = ex;
            throw;
        }
        finally
        {
            // 取消後や設定途中の失敗でも、変更した可能性がある位置の復元を試みます。
            if (attempted)
            {
                try
                {
                    WindowsOperationErrors.Provider(() => scroll.SetScrollPercent(-1, saved));
                }
                catch (Exception cleanup) when (primary is not null)
                {
                    primary.Data["UWSCSharp.UiaScrollRestoreError"] = cleanup;
                }
            }
        }

        token.ThrowIfCancellationRequested();
    }

    private static string? ReadListItems(IUiaAutomation automation, IUiaElement target,
        List<object> owned, CancellationToken token)
    {
        IUiaCondition listItem = WindowsOperationErrors.ProviderReference(
            () => automation.CreatePropertyCondition(30003, 50007));
        owned.Add(listItem);
        IUiaElements items = WindowsOperationErrors.ProviderReference(() => target.FindAll(2, listItem));
        owned.Add(items);
        int count = WindowsOperationErrors.Provider(() => items.Length());
        if (count == 0)
        {
            token.ThrowIfCancellationRequested();
            items = WindowsOperationErrors.ProviderReference(() => target.FindAll(4, listItem));
            owned.Add(items);
            count = WindowsOperationErrors.Provider(() => items.Length());
        }

        if (count < 0 || count > UWSCSharp.Core.UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("UIA選択項目数が上限を超えました。"));
        }

        if (count == 0)
        {
            token.ThrowIfCancellationRequested();
            return null;
        }

        IUiaCondition textKind = WindowsOperationErrors.ProviderReference(
            () => automation.CreatePropertyCondition(30003, 50020));
        owned.Add(textKind);
        var result = new StringBuilder();
        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            IUiaElement item = WindowsOperationErrors.ProviderReference(() => items.GetElement(index));
            Exception? cleanupPrimary2 = null;
            try
            {
                object? selected = WindowsOperationErrors.Provider(() => item.Property(30079));
                bool isSelected;
                Exception? cleanupPrimary3 = null;
                try
                {
                    isSelected = selected is true;
                }
                catch (Exception failure3)
                {
                    cleanupPrimary3 = failure3;
                    throw;
                }
                finally
                {
                    var cleanup3 = new ProviderCleanup(cleanupPrimary3);
                    cleanup3.ReleaseComOnly(selected);
                    cleanup3.Finish();
                }

                if (!isSelected)
                {
                    continue;
                }

                string text = ReadItemName(item, textKind);

                int separator = result.Length == 0 ? 0 : 1;
                if (text.Length > NativeListControl.MaximumCharacters - result.Length - separator)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("UIA選択文字数が上限を超えました。"));
                }

                if (separator != 0)
                {
                    result.Append('\t');
                }

                result.Append(text);
            }
            catch (Exception failure2)
            {
                cleanupPrimary2 = failure2;
                throw;
            }
            finally
            {
                var cleanup2 = new ProviderCleanup(cleanupPrimary2);
                cleanup2.Release(item);
                cleanup2.Finish();
            }
        }

        token.ThrowIfCancellationRequested();
        return result.ToString();
    }

    private static string StringProperty(IUiaElement item, int property)
    {
        object? value = WindowsOperationErrors.Provider(() => item.Property(property));
        Exception? cleanupPrimary5 = null;
        try
        {
            string text = value as string ?? "";
            if (text.Length > NativeListControl.MaximumCharacters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("UIA選択文字数が上限を超えました。"));
            }

            return text;
        }
        catch (Exception failure5)
        {
            cleanupPrimary5 = failure5;
            throw;
        }
        finally
        {
            var cleanup5 = new ProviderCleanup(cleanupPrimary5);
            cleanup5.ReleaseComOnly(value);
            cleanup5.Finish();
        }
    }
}

[ComImport]
[Guid("5ed5202e-b2ac-47a6-b638-4b0bf140d78e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaSelectionPattern
{
    IUiaElements GetCurrentSelection();
}

[ComImport]
[Guid("88f4d42a-e881-459d-a77c-73bbbb7e02dc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaScrollPattern
{
    void Scroll(int horizontalAmount, int verticalAmount);

    void SetScrollPercent(double horizontalPercent, double verticalPercent);

    double HorizontalScrollPercent();

    double VerticalScrollPercent();

    double HorizontalViewSize();

    double VerticalViewSize();

    [return: MarshalAs(UnmanagedType.Bool)]
    bool HorizontallyScrollable();

    [return: MarshalAs(UnmanagedType.Bool)]
    bool VerticallyScrollable();
}

[ComImport]
[Guid("619be086-1f4e-4ee4-bafa-210128738730")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaExpandCollapsePattern
{
    void Expand();

    void Collapse();

    int State();
}
