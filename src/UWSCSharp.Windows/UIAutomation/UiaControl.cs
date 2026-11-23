using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

[SupportedOSPlatform("windows")]
internal static class UiaControl
{
    internal static Task<int> ReadCheckAsync(nint root, string name,
        int occurrence, CancellationToken token,
        UiaMozillaPreparation? preparation = null) => Task.Run(() =>
            preparation is null ? ReadCheck(root, name, occurrence, token)
                : preparation.Execute(root, prepare =>
                    ReadCheck(root, name, occurrence, token, prepare), token), token);

    private static int ReadCheck(nint root, string name, int occurrence,
        CancellationToken token, Action? prepare = null)
    {
        token.ThrowIfCancellationRequested();
        if (root == 0 || !Native.IsWindow(root))
        {
            return -1;
        }

        List<object> owned = [];
        Exception? cleanupPrimary1 = null;
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
                element = WindowsOperationErrors.ProviderReference(
                    () => automation.ElementFromHandle(root));
                owned.Add(element);
            }

            IUiaCondition enabled = WindowsOperationErrors.ProviderReference(
                () => automation.CreatePropertyCondition(30010, true));
            owned.Add(enabled);
            string desired = ButtonName.Normalize(name);
            int matched = 0;
            int characters = 0;
            int candidates = 0;
            foreach (int type in new[] { 50000, 50002, 50013 })
            {
                token.ThrowIfCancellationRequested();
                int start = owned.Count;
                Exception? cleanupPrimary2 = null;
                try
                {
                    IUiaCondition kind = WindowsOperationErrors.ProviderReference(
                        () => automation.CreatePropertyCondition(30003, type));
                    owned.Add(kind);
                    IUiaCondition condition = WindowsOperationErrors.ProviderReference(
                        () => automation.CreateAndCondition(kind, enabled));
                    owned.Add(condition);
                    IUiaElements children = WindowsOperationErrors.ProviderReference(
                        () => element.FindAll(7, condition));
                    owned.Add(children);
                    int count = WindowsOperationErrors.Provider(() => children.Length());
                    if (count < 0 || count > 100000 - candidates)
                    {
                        throw WindowsOperationErrors.Record(
                            new InvalidOperationException("UIA候補数が上限を超えました。"));
                    }

                    candidates += count;
                    for (int index = 0; index < count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        IUiaElement child = WindowsOperationErrors.ProviderReference(
                            () => children.GetElement(index));
                        Exception? cleanupPrimary3 = null;
                        try
                        {
                            string caption = ItemName(child);
                            if (caption.Length > NativeListControl.MaximumCharacters - characters)
                            {
                                throw WindowsOperationErrors.Record(
                                    new InvalidOperationException("UIAの判定文字数上限を超えました。"));
                            }

                            characters += caption.Length;
                            if (!AccessibleItems.MatchesName(caption, desired) || ++matched != occurrence)
                            {
                                continue;
                            }

                            int value = type == 50013 ? Selected(child) : ToggleState(child);
                            if (type == 50000)
                            {
                                value = value == 1 ? 1 : 0;
                            }

                            if (!BooleanProperty(child, 30010))
                            {
                                value = -1;
                            }

                            token.ThrowIfCancellationRequested();
                            return value;
                        }
                        catch (Exception failure3)
                        {
                            cleanupPrimary3 = failure3;
                            throw;
                        }
                        finally
                        {
                            var cleanup3 = new ProviderCleanup(cleanupPrimary3);
                            cleanup3.Release(child);
                            cleanup3.Finish();
                        }
                    }
                }
                catch (Exception failure2)
                {
                    cleanupPrimary2 = failure2;
                    throw;
                }
                finally
                {
                    var cleanup2 = new ProviderCleanup(cleanupPrimary2);
                    for (int index = owned.Count - 1; index >= start; index--)
                    {
                        cleanup2.Release(owned[index]);
                    }

                    owned.RemoveRange(start, owned.Count - start);
                    cleanup2.Finish();
                }
            }

            token.ThrowIfCancellationRequested();
            return -1;
        }
        catch (Exception failure1)
        {
            cleanupPrimary1 = failure1;
            throw;
        }
        finally
        {
            var cleanup1 = new ProviderCleanup(cleanupPrimary1);
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                cleanup1.Release(owned[index]);
            }
            cleanup1.Finish();
        }
    }

    private static int ToggleState(IUiaElement element)
    {
        object? pattern = null;
        Exception? cleanupPrimary4 = null;
        try
        {
            Guid iid = typeof(IUiaToggle).GUID;
            pattern = WindowsOperationErrors.Provider(() => element.PatternAs(10015, ref iid));
            if (pattern is not IUiaToggle toggle)
            {
                return -1;
            }

            int state = WindowsOperationErrors.Provider(() => toggle.CurrentState());
            return state is >= 0 and <= 2 ? state
                : throw WindowsOperationErrors.Record(
                    new InvalidOperationException("UIA Toggle状態が範囲外です。"));
        }
        catch (COMException error) when (error.HResult is
            unchecked((int)0x80040204) or unchecked((int)0x80004002))
        {
            return -1;
        }
        catch (Exception failure4)
        {
            cleanupPrimary4 = failure4;
            throw;
        }
        finally
        {
            var cleanup4 = new ProviderCleanup(cleanupPrimary4);
            if (pattern is not null)
            {
                cleanup4.Release(pattern);
            }
            cleanup4.Finish();
        }
    }

    private static int Selected(IUiaElement element)
    {
        return BooleanProperty(element, 30079) ? 1 : 0;
    }

    private static bool BooleanProperty(IUiaElement element, int property)
    {
        object? value = WindowsOperationErrors.Provider(() => element.Property(property));
        Exception? cleanupPrimary5 = null;
        try
        {
            return value is true;
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

    internal static Task<List<string>> ReadLinksAsync(nint root,
        bool includeDisabled, CancellationToken token, Action<string>? onItem = null) => Task.Run(() =>
        ReadLinks(root, includeDisabled, token, onItem), token);

    private static List<string> ReadLinks(nint root,
        bool includeDisabled, CancellationToken token, Action<string>? onItem = null)
    {
        token.ThrowIfCancellationRequested();
        List<string> result = [];
        if (root == 0 || !Native.IsWindow(root))
        {
            return result;
        }

        List<object> owned = [];
        Exception? cleanupPrimary6 = null;
        try
        {
            IUiaAutomation automation = WindowsOperationErrors.Provider(
                () => (IUiaAutomation)new UiaComObject());
            owned.Add(automation);
            IUiaElement element = WindowsOperationErrors.ProviderReference(
                () => automation.ElementFromHandle(root));
            owned.Add(element);
            IUiaCondition condition = WindowsOperationErrors.ProviderReference(
                () => automation.CreateTrueCondition());
            owned.Add(condition);
            IUiaElements children = WindowsOperationErrors.ProviderReference(
                () => element.FindAll(7, condition));
            owned.Add(children);
            int count = WindowsOperationErrors.Provider(() => children.Length());
            if (count < 0 || count > 100000)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("UIA候補数が上限を超えました。"));
            }

            int characters = 0;
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                IUiaElement child = WindowsOperationErrors.ProviderReference(
                    () => children.GetElement(index));
                Exception? cleanupPrimary7 = null;
                try
                {
                    if (WindowsOperationErrors.Provider(() => child.Property(30003)) is not int type || type != 50005
                        || (!includeDisabled && WindowsOperationErrors.Provider(() => child.Property(30010)) is not true))
                    {
                        continue;
                    }

                    string caption = ItemName(child);
                    if (caption.Length > NativeListControl.MaximumCharacters - characters)
                    {
                        throw WindowsOperationErrors.Record(
                            new InvalidOperationException("UIA LINKの文字列取得上限を超えました。"));
                    }

                    characters += caption.Length;
                    result.Add(caption);
                    onItem?.Invoke(caption);
                }
                catch (Exception failure7)
                {
                    cleanupPrimary7 = failure7;
                    throw;
                }
                finally
                {
                    var cleanup7 = new ProviderCleanup(cleanupPrimary7);
                    cleanup7.Release(child);
                    cleanup7.Finish();
                }
            }

            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception failure6)
        {
            cleanupPrimary6 = failure6;
            throw;
        }
        finally
        {
            var cleanup6 = new ProviderCleanup(cleanupPrimary6);
            for (int index = owned.Count - 1; index >= 0; index--)
            {
                cleanup6.Release(owned[index]);
            }
            cleanup6.Finish();
        }
    }

    private static string ItemName(IUiaElement element)
    {
        object? value = WindowsOperationErrors.Provider(() => element.Property(30005));
        Exception? cleanupPrimary8 = null;
        try
        {
            return value switch
            {
                null => "",
                string text => text,
                _ => throw WindowsOperationErrors.Record(
                    new InvalidOperationException("UIA Nameが文字列ではありません。"))
            };
        }
        catch (Exception failure8)
        {
            cleanupPrimary8 = failure8;
            throw;
        }
        finally
        {
            var cleanup8 = new ProviderCleanup(cleanupPrimary8);
            // 不正providerが返したIUnknownも取得したworkerで解放します。
            cleanup8.ReleaseComOnly(value);
            cleanup8.Finish();
        }
    }

    internal static Task<bool> InvokeAsync(nint root, string name,
        int occurrence, CancellationToken token, int types = 1,
        bool focusLink = false, bool on = true, Action? onMatch = null,
        Action? onCandidate = null, Func<IUiaElement, bool?>? pointerAction = null,
        bool partial = false) => Task.Run(() =>
        Invoke(root, name, occurrence, types, focusLink, on, token, onMatch,
            onCandidate, pointerAction, partial), token);

    private static bool Invoke(nint root, string name, int occurrence,
        int types, bool focusLink, bool on, CancellationToken token, Action? onMatch,
        Action? onCandidate, Func<IUiaElement, bool?>? pointerAction, bool partial)
    {
        token.ThrowIfCancellationRequested();
        if (root == 0 || !Native.IsWindow(root))
        {
            return false;
        }

        List<object> owned = [];
        Exception? cleanupPrimary9 = null;
        try
        {
            IUiaAutomation automation = WindowsOperationErrors.Provider(
                () => (IUiaAutomation)new UiaComObject());
            owned.Add(automation);
            IUiaElement element = WindowsOperationErrors.ProviderReference(
                () => automation.ElementFromHandle(root));
            owned.Add(element);
            IUiaCondition condition = WindowsOperationErrors.ProviderReference(
                () => automation.CreateTrueCondition());
            owned.Add(condition);
            IUiaElements children = WindowsOperationErrors.ProviderReference(
                () => element.FindAll(4, condition));
            owned.Add(children);
            IUiaTreeWalker walker = WindowsOperationErrors.ProviderReference(
                () => automation.RawViewWalker());
            owned.Add(walker);
            int count = WindowsOperationErrors.Provider(() => children.Length());
            if (count < 0 || count > 100000)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("UIA候補数が上限を超えました。"));
            }

            string desired = ButtonName.Normalize(name);
            foreach (int requestedType in new[] { 50000, 50005 })
            {
                int flag = requestedType == 50000 ? 1 : 128;
                if ((types & flag) == 0)
                {
                    continue;
                }

                int matched = 0;
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    IUiaElement child = WindowsOperationErrors.ProviderReference(
                        () => children.GetElement(i));
                    Exception? cleanupPrimary10 = null;
                    try
                    {
                        if (WindowsOperationErrors.Provider(() => child.Property(30003)) is not int controlType || controlType != requestedType

                                || WindowsOperationErrors.Provider(() => child.Property(30010)) is not true
                                || WindowsOperationErrors.Provider(() => child.Property(30022)) is true
                            || WindowsOperationErrors.Provider(() => child.Property(30031)) is not true
                            || WindowsOperationErrors.Provider(() => child.Property(30005)) is not string caption
                            || !(partial
                                ? ButtonName.Normalize(caption).Contains(desired, StringComparison.OrdinalIgnoreCase)
                                : ButtonName.Normalize(caption).Equals(desired, StringComparison.OrdinalIgnoreCase))
                            || (requestedType == 50005 && !AvailableLink(child, root, walker, token, on)))
                        {
                            continue;
                        }

                        onCandidate?.Invoke();
                        if (++matched != occurrence)
                        {
                            continue;
                        }

                        token.ThrowIfCancellationRequested();
                        onMatch?.Invoke();
                        bool? pointerResult = pointerAction?.Invoke(child);
                        if (pointerResult.HasValue)
                        {
                            return pointerResult.Value;
                        }

                        if (requestedType == 50005 && focusLink)
                        {
                            WindowsOperationErrors.Provider(() => child.SetFocus());
                            token.ThrowIfCancellationRequested();
                        }

                        // LINKのOFFは照合と条件付きfocusだけで、既定操作は行いません。
                        if (!on)
                        {
                            return true;
                        }

                        Guid iid = typeof(IUiaInvoke).GUID;
                        object pattern = WindowsOperationErrors.Provider(
                            () => child.PatternAs(10000, ref iid));
                        Exception? cleanupPrimary11 = null;
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                WindowsOperationErrors.Provider(() => ((IUiaInvoke)pattern).Invoke());
                            }
                            catch (InvalidOperationException exception) when (
                                requestedType == 50005
                                && exception.HResult == unchecked((int)0x80131509))
                            {
                                token.ThrowIfCancellationRequested();
                                return false;
                            }

                            token.ThrowIfCancellationRequested();
                            return true;
                        }
                        catch (Exception failure11)
                        {
                            cleanupPrimary11 = failure11;
                            throw;
                        }
                        finally
                        {
                            var cleanup11 = new ProviderCleanup(cleanupPrimary11);
                            cleanup11.Release(pattern);
                            cleanup11.Finish();
                        }
                    }
                    catch (Exception failure10)
                    {
                        cleanupPrimary10 = failure10;
                        throw;
                    }
                    finally
                    {
                        var cleanup10 = new ProviderCleanup(cleanupPrimary10);
                        cleanup10.Release(child);
                        cleanup10.Finish();
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (COMException exception) when (exception.HResult is unchecked((int)0x80040200)
            or unchecked((int)0x80040201))
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception failure9)
        {
            cleanupPrimary9 = failure9;
            throw;
        }
        finally
        {
            var cleanup9 = new ProviderCleanup(cleanupPrimary9);
            for (int i = owned.Count - 1; i >= 0; i--)
            {
                cleanup9.Release(owned[i]);
            }
            cleanup9.Finish();
        }
    }

    private static bool AvailableLink(IUiaElement child, nint root,
        IUiaTreeWalker walker, CancellationToken token, bool invoking)
    {
        List<IUiaElement> parents = [];
        Exception? cleanupPrimary12 = null;
        try
        {
            IUiaElement current = child;
            for (int depth = 0; depth < 128; depth++)
            {
                token.ThrowIfCancellationRequested();
                if (WindowsOperationErrors.Provider(() => current.Property(30010)) is false
                    || WindowsOperationErrors.Provider(() => current.Property(30022)) is true)
                {
                    return false;
                }

                if (WindowsOperationErrors.Provider(() => current.Property(30020)) is int handle && handle != 0)
                {
                    nint window = unchecked((nint)(uint)handle);
                    if (!Native.IsWindow(window) || !Native.IsWindowVisible(window)
                        || !Native.IsWindowEnabled(window))
                    {
                        return false;
                    }

                    if (invoking && !NativeLinkAvailability.CanInvoke(window))
                    {
                        return false;
                    }

                    if (window == root)
                    {
                        return true;
                    }
                }

                IUiaElement? parent = WindowsOperationErrors.Provider(() => walker.Parent(current));
                if (parent is null)
                {
                    return false;
                }

                parents.Add(parent);
                current = parent;
            }

            throw WindowsOperationErrors.Record(
                new InvalidOperationException("UIA LINKの祖先探索深度が上限を超えました。"));
        }
        catch (Exception failure12)
        {
            cleanupPrimary12 = failure12;
            throw;
        }
        finally
        {
            var cleanup12 = new ProviderCleanup(cleanupPrimary12);
            for (int index = parents.Count - 1; index >= 0; index--)
            {
                cleanup12.Release(parents[index]);
            }
            cleanup12.Finish();
        }
    }
}

[ComImport]
[Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
internal class UiaComObject;

// UIAutomationClient.hのIUnknown後の順序を維持します。
[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaAutomation
{
    void CompareElements(IUiaElement first, IUiaElement second, out int same);

    void CompareRuntimeIds(nint first, nint second, out int same);

    IUiaElement GetRootElement();

    IUiaElement ElementFromHandle(nint window);

    IUiaElement ElementFromPoint(Native.Point point);

    IUiaElement GetFocusedElement();

    IUiaElement GetRootElementBuildCache(nint cache);

    IUiaElement ElementFromHandleBuildCache(nint window, nint cache);

    IUiaElement ElementFromPointBuildCache(Native.Point point, nint cache);

    IUiaElement GetFocusedElementBuildCache(nint cache);

    nint CreateTreeWalker(IUiaCondition condition);

    nint ControlViewWalker();

    nint ContentViewWalker();

    IUiaTreeWalker RawViewWalker();

    IUiaCondition RawViewCondition();

    IUiaCondition ControlViewCondition();

    IUiaCondition ContentViewCondition();

    nint CreateCacheRequest();

    IUiaCondition CreateTrueCondition();

    IUiaCondition CreateFalseCondition();

    IUiaCondition CreatePropertyCondition(int property,
        [MarshalAs(UnmanagedType.Struct)] object value);

    IUiaCondition CreatePropertyConditionEx(int property,
        [MarshalAs(UnmanagedType.Struct)] object value, int flags);

    IUiaCondition CreateAndCondition(IUiaCondition first, IUiaCondition second);
}

[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaElement
{
    void SetFocus();

    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
    int[] RuntimeId();

    IUiaElement? FindFirst(int scope, IUiaCondition condition);

    IUiaElements FindAll(int scope, IUiaCondition condition);

    IUiaElement? FindFirstBuildCache(int scope, IUiaCondition condition, nint cache);

    IUiaElements FindAllBuildCache(int scope, IUiaCondition condition, nint cache);

    IUiaElement BuildUpdatedCache(nint cache);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? Property(int property);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? PropertyEx(int property, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefault);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? CachedProperty(int property);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? CachedPropertyEx(int property, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefault);

    [return: MarshalAs(UnmanagedType.IUnknown)]
    object PatternAs(int pattern, ref Guid iid);
}

[ComImport]
[Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaCondition;

[ComImport]
[Guid("4042c624-389c-4afc-a630-9df854a541fc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaTreeWalker
{
    IUiaElement? Parent(IUiaElement element);
}

[ComImport]
[Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaElements
{
    int Length();

    IUiaElement GetElement(int index);
}

[ComImport]
[Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaInvoke
{
    void Invoke();
}

[ComImport]
[Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaToggle
{
    // vtable順序のために宣言し、状態取得では呼びません。
    void Toggle();

    int CurrentState();
}
