using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// UIAのSliderとPaneのスクロール軸を共通の番号で扱います。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UiaSlider
{
    internal static Task<double> ExecuteAsync(nint root, int number,
        int? position, int item, CancellationToken token,
        UiaMozillaPreparation? preparation = null) => Task.Run(() =>
            preparation is null ? Execute(root, number, position, item, token)
                : preparation.Execute(root, prepare =>
                    Execute(root, number, position, item, token, prepare), token), token);

    private static double Execute(nint root, int number,
        int? position, int item, CancellationToken token, Action? prepare = null)
    {
        token.ThrowIfCancellationRequested();
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
                element = WindowsOperationErrors.ProviderReference(
                    () => automation.ElementFromHandle(root));
                owned.Add(element);
            }

            long occurrence = number == 0 ? 1 : Math.Abs((long)number);
            int matched = 0;
            int candidates = 0;
            foreach (int type in new[] { 50015, 50033 })
            {
                token.ThrowIfCancellationRequested();
                IUiaCondition condition = WindowsOperationErrors.ProviderReference(
                    () => automation.CreatePropertyCondition(30003, type));
                owned.Add(condition);
                IUiaElements children = WindowsOperationErrors.ProviderReference(
                    () => element.FindAll(4, condition));
                owned.Add(children);
                int count = WindowsOperationErrors.Provider(() => children.Length());
                if (count < 0 || count > 100000 - candidates)
                {
                    throw Invalid("UIAスライダー候補数が上限を超えました。");
                }

                candidates += count;
                for (int index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    IUiaElement child = WindowsOperationErrors.ProviderReference(
                        () => children.GetElement(index));
                    object? pattern = null;
                    Exception? childPrimary = null;
                    try
                    {
                        if (type == 50015)
                        {
                            if (++matched != occurrence)
                            {
                                continue;
                            }

                            pattern = Pattern(child, 10003, typeof(IUiaRangeValue).GUID);
                            if (pattern is IUiaRangeValue range)
                            {
                                return Range(child, range, root, position, item, token);
                            }

                            continue;
                        }

                        pattern = Pattern(child, 10004, typeof(IUiaScrollPattern).GUID);
                        if (pattern is not IUiaScrollPattern scroll)
                        {
                            continue;
                        }

                        // 原版のUIA経路と同じく、縦軸を横軸より先に数えます。
                        foreach (bool vertical in new[] { true, false })
                        {
                            token.ThrowIfCancellationRequested();
                            bool scrollable = WindowsOperationErrors.Provider(() =>
                                vertical ? scroll.VerticallyScrollable()
                                    : scroll.HorizontallyScrollable());
                            if (scrollable && ++matched == occurrence)
                            {
                                return Scroll(child, scroll, root, vertical,
                                    position, item, token);
                            }
                        }
                    }
                    catch (Exception failure)
                    {
                        childPrimary = failure;
                        throw;
                    }
                    finally
                    {
                        var cleanup = new ProviderCleanup(childPrimary);
                        cleanup.ReleaseComOnly(pattern);
                        cleanup.Release(child);
                        cleanup.Finish();
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            return position.HasValue ? 0 : -999999;
        }
        catch (Exception failure)
        {
            primary = failure;
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

    private static object? Pattern(IUiaElement element, int id, Guid iid)
    {
        try
        {
            return WindowsOperationErrors.Provider(() => element.PatternAs(id, ref iid));
        }
        catch (COMException failure) when (failure.HResult is
            unchecked((int)0x80040204) or unchecked((int)0x80004002))
        {
            return null;
        }
    }

    internal static double Range(IUiaElement element, IUiaRangeValue range,
        nint root, int? position, int item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (position is int requested)
        {
            return SetRange(range, Enabled(element), requested, token);
        }

        double value = item switch
        {
            1 => WindowsOperationErrors.Provider(range.Minimum),
            2 => WindowsOperationErrors.Provider(range.Maximum),
            3 => WindowsOperationErrors.Provider(range.LargeChange),
            >= 4 => Geometry(element, root, item),
            _ => WindowsOperationErrors.Provider(range.Value)
        };
        token.ThrowIfCancellationRequested();
        return Integer(value);
    }

    internal static double SetRange(IUiaRangeValue range, bool enabled,
        int requested, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool readOnly = enabled && WindowsOperationErrors.Provider(range.IsReadOnly);
        token.ThrowIfCancellationRequested();
        if (!enabled || readOnly)
        {
            return 0;
        }

        double minimum = Finite(WindowsOperationErrors.Provider(range.Minimum));
        double maximum = Finite(WindowsOperationErrors.Provider(range.Maximum));
        if (minimum > maximum)
        {
            throw Invalid("UIAスライダーの範囲が逆転しています。");
        }

        double target = Math.Clamp(requested, minimum, maximum);
        double before = Finite(WindowsOperationErrors.Provider(range.Value));
        token.ThrowIfCancellationRequested();
        if (before != target)
        {
            WindowsOperationErrors.Provider(() => range.SetValue(target));
        }

        token.ThrowIfCancellationRequested();
        double after = Finite(WindowsOperationErrors.Provider(range.Value));
        token.ThrowIfCancellationRequested();
        return after == target ? 1 : 0;
    }

    private static double Scroll(IUiaElement element, IUiaScrollPattern scroll,
        nint root, bool vertical, int? position, int item, CancellationToken token)
    {
        double ReadPosition() => Finite(WindowsOperationErrors.Provider(() =>
            vertical ? scroll.VerticalScrollPercent() : scroll.HorizontalScrollPercent()));

        if (position is int requested)
        {
            if (!Enabled(element))
            {
                return 0;
            }

            double target = Math.Clamp(requested, 0, 100);
            double before = ReadPosition();
            token.ThrowIfCancellationRequested();
            if (before != target)
            {
                WindowsOperationErrors.Provider(() => scroll.SetScrollPercent(
                    vertical ? -1 : target, vertical ? target : -1));
            }

            token.ThrowIfCancellationRequested();
            double after = ReadPosition();
            token.ThrowIfCancellationRequested();
            return after == target ? 1 : 0;
        }

        double value = item switch
        {
            1 => 0,
            2 => 100,
            3 => WindowsOperationErrors.Provider(() => vertical
                ? scroll.VerticalViewSize() : scroll.HorizontalViewSize()),
            4 => vertical ? 1 : 0,
            >= 5 => Geometry(element, root, item),
            _ => ReadPosition()
        };
        token.ThrowIfCancellationRequested();
        return Integer(value);
    }

    private static bool Enabled(IUiaElement element) =>
        WindowsOperationErrors.Provider(() => element.Property(30010)) is true;

    private static double Geometry(IUiaElement element, nint root, int item)
    {
        using var dpi = new PhysicalDpiScope();
        if (WindowsOperationErrors.Provider(() => element.Property(30001))
            is not double[] bounds || bounds.Length != 4
            || bounds.Any(value => !double.IsFinite(value))
            || bounds[2] < 0 || bounds[3] < 0)
        {
            throw Invalid("UIAスライダーの矩形が不正です。");
        }

        if (item == 4)
        {
            return bounds[2] < bounds[3] ? 1 : 0;
        }

        var point = new Native.Point(Integer(bounds[0] + (item >= 7 ? bounds[2] : 0)),
            Integer(bounds[1] + (item >= 7 ? bounds[3] : 0)));
        if (!Native.ScreenToClient(root, ref point))
        {
            throw WindowsOperationErrors.Record(
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
        return item is 5 or 7 ? point.X : point.Y;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value
        : throw Invalid("UIAスライダーが有限でない値を返しました。");

    private static int Integer(double value) => WindowsOperationErrors.Calculate(
        () => checked((int)Math.Truncate(Finite(value))));

    private static InvalidOperationException Invalid(string message) =>
        WindowsOperationErrors.Record(new InvalidOperationException(message));
}

// Windows SDKのUIAutomationClient.hにあるIUnknown後の順序を維持します。
[ComImport]
[Guid("59213f4f-7346-49e5-b120-80555987a148")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaRangeValue
{
    void SetValue(double value);

    double Value();

    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsReadOnly();

    double Maximum();

    double Minimum();

    double LargeChange();
}
