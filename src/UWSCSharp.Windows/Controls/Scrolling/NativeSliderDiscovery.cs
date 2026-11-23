using System.Runtime.ExceptionServices;

namespace UWSCSharp.Windows;

internal enum NativeSliderKind
{
    None,
    Trackbar,
    ScrollBar
}

internal static class NativeSliderDiscovery
{
    internal static NativeSliderKind Kind(string className)
    {
        if (className.Contains("TRACKBAR", StringComparison.OrdinalIgnoreCase))
        {
            return NativeSliderKind.Trackbar;
        }

        return className.Contains("SCROLLBAR", StringComparison.OrdinalIgnoreCase)
            ? NativeSliderKind.ScrollBar : NativeSliderKind.None;
    }

    internal static void VisitChildren(nint root, Action<nint> visit,
        CancellationToken token,
        Func<Native.EnumCallback, bool>? enumerate = null)
    {
        token.ThrowIfCancellationRequested();
        Exception? failure = null;
        bool Callback(nint child, nint data)
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    return false;
                }

                visit(child);
                return !token.IsCancellationRequested;
            }
            catch (Exception error)
            {
                // 管理例外をWin32のcallback境界へ出さず、列挙後に再送出します。
                failure = error;
                return false;
            }
        }

        if (enumerate is null)
        {
            _ = Native.EnumChildWindows(root, Callback, 0);
        }
        else
        {
            _ = enumerate(Callback);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        token.ThrowIfCancellationRequested();
    }
}
