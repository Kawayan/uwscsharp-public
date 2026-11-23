using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

internal interface IButtonPointWindowPlatform
{
    double LocalDate();

    bool Enumerate(Func<nint, bool> visitor);

    bool Visible(nint window);

    nint AtPoint(Native.Point point);

    nint Parent(nint window);

    Native.Rect Bounds(nint window);
}

/// <summary>
/// BTN補助の可視top-level cacheと点windowの親探索を保持します。
/// </summary>
internal sealed class NativeButtonPointWindow(IButtonPointWindowPlatform platform)
{
    private readonly object gate = new();
    private readonly List<nint> windows = [];
    private double sampledAt;

    internal nint Find(Native.Point point, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint current = platform.AtPoint(point);
        for (int count = 0; count < 100; count++)
        {
            token.ThrowIfCancellationRequested();
            if (Contains(current, token))
            {
                break;
            }

            nint parent = platform.Parent(current);
            token.ThrowIfCancellationRequested();
            if (parent == 0)
            {
                break;
            }

            Native.Rect bounds = platform.Bounds(parent);
            token.ThrowIfCancellationRequested();
            if (bounds.Left == bounds.Right || bounds.Top == bounds.Bottom)
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    internal void ResetTimestamp()
    {
        lock (gate)
        {
            sampledAt = 0;
        }
    }

    private bool Contains(nint window, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            // 時計の巻戻しも原本と同じ差分比較で扱います。
            if (!IsFresh(platform.LocalDate(), sampledAt))
            {
                windows.Clear();
                OperationCanceledException? cancellation = null;
                platform.Enumerate(candidate =>
                {
                    // unmanaged callbackから取消例外を外へ投げません。
                    if (token.IsCancellationRequested)
                    {
                        return false;
                    }

                    try
                    {
                        if (platform.Visible(candidate))
                        {
                            windows.Add(candidate);
                        }
                    }
                    catch (OperationCanceledException error)
                    {
                        cancellation = error;
                        return false;
                    }
                    catch (Exception)
                    {
                        // 原本のcollectorは各windowの通常例外を抑止して続けます。
                    }

                    return !token.IsCancellationRequested;
                });
                if (cancellation is not null)
                {
                    ExceptionDispatchInfo.Capture(cancellation).Throw();
                }

                token.ThrowIfCancellationRequested();
                // 列挙APIがfalseでも得られた候補と列挙後の時刻を保持します。
                sampledAt = platform.LocalDate();
            }

            token.ThrowIfCancellationRequested();
            return windows.Contains(window);
        }
    }

    internal static bool IsFresh(double current, double previous)
    {
        double difference = current - previous;
        double seconds = 1d / 86400d;
        if (difference != seconds)
        {
            return difference < seconds;
        }

        // 原本80bit定数はdoubleの1秒より695×2^-80だけ大きい値です。
        // 境界では減算の丸め誤差も復元して、単純な<=への置換を避けます。
        double previousPart = current - difference;
        double currentPart = difference + previousPart;
        double error = (current - currentPart) + (previousPart - previous);
        double extendedUnits = Math.Round(Math.ScaleB(error, 80),
            MidpointRounding.ToEven);
        return extendedUnits < 695;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class NativeButtonPointWindowPlatform : IButtonPointWindowPlatform
{
    private readonly NativeListSelectionPlatform windows = new();

    public double LocalDate() => DateTime.Now.ToOADate();

    public bool Enumerate(Func<nint, bool> visitor)
        => Native.EnumWindows((window, _) => visitor(window), 0);

    public bool Visible(nint window) => Native.IsWindowVisible(window);

    public nint AtPoint(Native.Point point) => Native.WindowFromPoint(point);

    public nint Parent(nint window) => Native.GetParent(window);

    public Native.Rect Bounds(nint window) => windows.Bounds(window);
}
