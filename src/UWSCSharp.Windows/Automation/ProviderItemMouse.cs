using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// providerの項目矩形を使い、物理マウス入力を送ります。
/// </summary>
internal sealed class ProviderItemMouse(Action<Native.Input> send,
    bool swapped, IListSelectionPlatform platform)
{
    internal const ClickItemFlags Buttons = ClickItemFlags.LeftClick
        | ClickItemFlags.RightClick | ClickItemFlags.DoubleClick;
    internal const ClickItemFlags Options = Buttons | ClickItemFlags.MouseMove;
    internal const string ReleaseErrorsKey = "UWSCSharp.ProviderMouseReleaseErrors";

    internal Native.Point? Move(double x, double y, double width,
        double height, ClickItemFlags flags, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "providerの項目矩形が不正です。"));
        }

        double left = platform.Metric(76);
        double top = platform.Metric(77);
        double right = left + platform.Metric(78);
        double bottom = top + platform.Metric(79);
        if (width <= 0 || height <= 0 || right <= left || bottom <= top
            || x >= right || y >= bottom || x + width <= left
            || y + height <= top)
        {
            return null;
        }

        double offset = (flags & Buttons) == 0 ? -1
            : width >= 17 && height >= 17 ? 8
            : width >= 9 && height >= 9 ? 4 : -1;
        double targetX = x + (offset < 0 ? width / 2 : offset);
        double targetY = y + (offset < 0 ? height / 2 : offset);
        var point = new Native.Point(
            WindowsOperationErrors.Calculate(() => checked((int)Math.Floor(
                Math.Clamp(targetX, left, right - 1)))),
            WindowsOperationErrors.Calculate(() => checked((int)Math.Floor(
                Math.Clamp(targetY, top, bottom - 1)))));
        platform.MoveCursor(point);
        token.ThrowIfCancellationRequested();
        return point;
    }

    internal bool Click(nint root, Native.Point point,
        ClickItemFlags flags, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if ((flags & Buttons) == 0)
        {
            return true;
        }

        nint top = Native.GetAncestor(root, 2);
        platform.Foreground(top == 0 ? root : top);
        platform.Sleep(20, token);
        bool left = (flags & ClickItemFlags.LeftClick) != 0;
        bool right = !left && (flags & ClickItemFlags.RightClick) != 0;
        bool twice = !left && !right && (flags & ClickItemFlags.DoubleClick) != 0;
        int button = right ? 1 : 0;
        if (swapped)
        {
            button = 1 - button;
        }

        if (!Pair())
        {
            return false;
        }
        if (twice)
        {
            platform.Sleep(100, token);
            return Pair();
        }

        return true;

        bool Pair()
        {
            token.ThrowIfCancellationRequested();
            platform.MoveCursor(point);
            // 最後の移動を対象が処理する時間を設け、押下を先行させません。
            platform.Sleep(20, token);
            token.ThrowIfCancellationRequested();
            // 移動後の実位置とヒット先を照会し、他のルートへ入力しません。
            bool owned = platform.OwnsPointer(root, point);
            token.ThrowIfCancellationRequested();
            if (!owned)
            {
                return false;
            }

            Exception? primary = null;
            try
            {
                Send(up: false);
                platform.Sleep(20, token);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception failure) when (
                ProviderCleanup.PreservePrimary(failure, ref primary))
            {
                throw;
            }
            finally
            {
                // 送信中の取消・失敗でもupを試み、元の失敗を保持します。
                try
                {
                    Send(up: true);
                }
                catch (Exception failure) when (primary is not null)
                {
                    primary.Data[ReleaseErrorsKey] = failure;
                }
            }

            token.ThrowIfCancellationRequested();
            return true;
        }

        void Send(bool up) => send(new Native.Input
        {
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput
                {
                    Flags = button == 0 ? up ? 4u : 2u : up ? 16u : 8u,
                    Extra = 1
                }
            }
        });
    }
}
