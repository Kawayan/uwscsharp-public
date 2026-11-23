using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 対象ウィンドウ内の編集欄・表示文字列をUIAで読み書きします。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UiaTextInput
{
    internal static Task<bool> WriteAsync(nint root, string text,
        int occurrence, int mode, CancellationToken token) => Task.Run(() =>
    {
        long remaining = Math.Abs((long)occurrence);
        return Visit(root, 50004, (automation, child) =>
        {
            if (!Matches(child, occurrence, ref remaining))
            {
                return TextControlValue.Missing;
            }

            return WithValue(child, value =>
            {
                if (WindowsOperationErrors.Provider(value.IsReadOnly))
                {
                    throw Invalid("UIA編集欄は読取専用です。");
                }

                string replacement = mode == 0
                    ? WindowsOperationErrors.Provider(value.Value) + text : text;
                CheckLength(replacement);
                token.ThrowIfCancellationRequested();
                WindowsOperationErrors.Provider(() => value.SetValue(replacement));
                return new(true, replacement);
            });
        }, token).Found;
    }, token);

    internal static Task<TextControlValue> ReadAsync(nint root,
        int occurrence, int kind, bool bounds, CancellationToken token) => Task.Run(() =>
    {
        // UIAの物理矩形をDPI非対応の呼出し元でも同じ座標で取得します。
        using PhysicalDpiScope? dpi = bounds ? new PhysicalDpiScope() : null;
        if (kind == 2)
        {
            long part = Math.Max(1, Math.Abs((long)occurrence));
            return Visit(root, 50017, (automation, status) =>
                occurrence >= 0 && WindowsOperationErrors.Provider(
                    () => status.Property(30010)) is not true
                    ? TextControlValue.Missing
                    : ReadStatusPart(automation, status, ref part, bounds, token), token);
        }

        long remaining = Math.Abs((long)occurrence);
        if (kind == 1)
        {
            remaining = Math.Max(1, remaining);
        }

        return Visit(root, kind == 0 ? 50004 : 50020, (automation, child) =>
        {
            if (!Matches(child, kind == 1 && occurrence == 0 ? 1 : occurrence,
                ref remaining))
            {
                return TextControlValue.Missing;
            }

            string text = kind == 1 ? Name(child)
                : WithValue(child, value => new TextControlValue(true,
                    WindowsOperationErrors.Provider(value.Value) ?? "")).Text;
            return Result(child, text, bounds);
        }, token);
    }, token);

    private static bool Matches(IUiaElement element,
        int occurrence, ref long remaining)
    {
        if (occurrence >= 0 && WindowsOperationErrors.Provider(
            () => element.Property(30010)) is not true)
        {
            return false;
        }

        return occurrence == 0 ? WindowsOperationErrors.Provider(
            () => element.Property(30008)) is true : --remaining == 0;
    }

    private static TextControlValue Visit(nint root, int type,
        Func<IUiaAutomation, IUiaElement, TextControlValue> select,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (root == 0 || !Native.IsWindow(root))
        {
            return TextControlValue.Missing;
        }

        List<object> owned = [];
        Exception? primary = null;
        try
        {
            IUiaAutomation automation = WindowsOperationErrors.Provider(
                () => (IUiaAutomation)new UiaComObject());
            owned.Add(automation);
            IUiaElement element;
            try
            {
                element = WindowsOperationErrors.ProviderReference(
                    () => automation.ElementFromHandle(root));
            }
            catch (COMException failure) when (
                failure.HResult == unchecked((int)0x80040201))
            {
                return TextControlValue.Missing;
            }

            owned.Add(element);
            IUiaCondition condition = WindowsOperationErrors.ProviderReference(
                () => automation.CreatePropertyCondition(30003, type));
            owned.Add(condition);
            IUiaElements children = WindowsOperationErrors.ProviderReference(
                () => element.FindAll(7, condition));
            owned.Add(children);
            int count = Count(children);
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                IUiaElement child = WindowsOperationErrors.ProviderReference(
                    () => children.GetElement(index));
                Exception? childPrimary = null;
                try
                {
                    TextControlValue result = select(automation, child);
                    token.ThrowIfCancellationRequested();
                    if (result.Found)
                    {
                        return result;
                    }
                }
                catch (Exception failure) when (
                    ProviderCleanup.PreservePrimary(failure, ref childPrimary))
                {
                    throw;
                }
                finally
                {
                    var cleanup = new ProviderCleanup(childPrimary);
                    cleanup.ReleaseComOnly(child);
                    cleanup.Finish();
                }
            }

            return TextControlValue.Missing;
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
                cleanup.ReleaseComOnly(owned[index]);
            }

            cleanup.Finish();
        }
    }

    private static TextControlValue ReadStatusPart(IUiaAutomation automation,
        IUiaElement status, ref long part, bool bounds, CancellationToken token)
    {
        IUiaCondition? condition = null;
        IUiaElements? children = null;
        IUiaElement? selected = null;
        Exception? primary = null;
        try
        {
            condition = WindowsOperationErrors.ProviderReference(
                () => automation.CreatePropertyCondition(30003, 50020));
            children = WindowsOperationErrors.ProviderReference(
                () => status.FindAll(2, condition));
            int count = Count(children);
            if (part > count)
            {
                part -= count;
                return TextControlValue.Missing;
            }

            int index = checked((int)part - 1);
            token.ThrowIfCancellationRequested();
            selected = WindowsOperationErrors.ProviderReference(
                () => children.GetElement(index));
            return Result(selected, Name(selected), bounds);
        }
        catch (Exception failure) when (
            ProviderCleanup.PreservePrimary(failure, ref primary))
        {
            throw;
        }
        finally
        {
            var cleanup = new ProviderCleanup(primary);
            cleanup.ReleaseComOnly(selected);
            cleanup.ReleaseComOnly(children);
            cleanup.ReleaseComOnly(condition);
            cleanup.Finish();
        }
    }

    private static TextControlValue WithValue(IUiaElement child,
        Func<IUiaValue, TextControlValue> use)
    {
        object? pattern = null;
        Exception? primary = null;
        try
        {
            Guid iid = typeof(IUiaValue).GUID;
            pattern = WindowsOperationErrors.ProviderReference(
                () => child.PatternAs(10002, ref iid));
            return pattern is IUiaValue value ? use(value)
                : throw Invalid("UIA編集欄の値パターンに対応していません。");
        }
        catch (Exception failure) when (
            ProviderCleanup.PreservePrimary(failure, ref primary))
        {
            throw;
        }
        finally
        {
            var cleanup = new ProviderCleanup(primary);
            cleanup.ReleaseComOnly(pattern);
            cleanup.Finish();
        }
    }

    private static TextControlValue Result(IUiaElement child,
        string text, bool bounds)
    {
        CheckLength(text);
        Native.Rect? location = null;
        if (bounds)
        {
            if (WindowsOperationErrors.Provider(() => child.Property(30001))
                is not double[] rectangle)
            {
                throw Invalid("UIA取得対象の矩形を取得できません。");
            }

            location = TextTargetGeometry.FromUia(rectangle);
        }

        return new(true, text, location);
    }

    private static string Name(IUiaElement child) =>
        WindowsOperationErrors.Provider(() => child.Property(30005)) as string ?? "";

    private static int Count(IUiaElements elements)
    {
        int count = WindowsOperationErrors.Provider(() => elements.Length());
        return count is >= 0 and <= 100000 ? count
            : throw Invalid("UIA候補数が上限を超えました。");
    }

    private static void CheckLength(string text)
    {
        if (text.Length > NativeListControl.MaximumCharacters)
        {
            throw Invalid("UIA文字数が上限を超えました。");
        }
    }

    private static InvalidOperationException Invalid(string message) =>
        WindowsOperationErrors.Record(new InvalidOperationException(message));
}

// Windows SDKのUIAutomationClient.hにあるIUnknown後の順序を維持します。
[ComImport]
[Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaValue
{
    void SetValue([MarshalAs(UnmanagedType.BStr)] string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string Value();

    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsReadOnly();
}
