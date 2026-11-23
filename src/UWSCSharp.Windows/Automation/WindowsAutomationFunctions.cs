using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    // InvokeAsyncから振り分ける関数ごとの処理です。
    private sealed partial class Session
    {

        private static Value N(double number) => Value.Numeric(number);

        /// <summary>
        /// 引数indexの待機時間だけ待ち、取消を確認します。
        /// </summary>
        private async Task DelayArgumentAsync(CallArguments args,
            int index, CancellationToken token)
        {
            if (args.Int(index) > 0)
            {
                await DelayMillisecondsAsync(args.Int(index),
                    token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
        }

        private async ValueTask<Value> InvokeGetOleItemAsync(
            CallArguments args, CancellationToken token)
        {
            IReadOnlyList<Value> snapshot = await Com.EnumerateAsync(
                args.Values[0], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            AllOleItems = snapshot;
            return N(snapshot.Count);
        }

        private Value InvokePosAcc(
            CallArguments args, CancellationToken token)
        {
            using var dpi = new PhysicalDpiScope();
            if (args.Int(0) == 0)
            {
                int left = Native.GetSystemMetrics(76);
                int top = Native.GetSystemMetrics(77);
                if (args.Int(1) < left || args.Int(2) < top
                    || (long)args.Int(1) >= (long)left + Native.GetSystemMetrics(78)
                    || (long)args.Int(2) >= (long)top + Native.GetSystemMetrics(79))
                {
                    return Value.String("");
                }

                return Value.String(AccessibleReader.Read(0,
                    args.Int(1), args.Int(2), args.Int(3) & 15, token));
            }

            nint accessibleRoot = Resolve(args.Int(0), false);
            if (accessibleRoot == 0)
            {
                return Value.String("");
            }

            Native.Rect accessibleRect = Rect(accessibleRoot, true);
            if (args.Int(1) < 0 || args.Int(2) < 0 || args.Int(1) >= accessibleRect.Right
                || args.Int(2) >= accessibleRect.Bottom)
            {
                return Value.String("");
            }

            var accessiblePoint = new Native.Point(args.Int(1), args.Int(2));
            Check(Native.ClientToScreen(accessibleRoot, ref accessiblePoint));
            return Value.String(AccessibleReader.Read(accessibleRoot,
                accessiblePoint.X, accessiblePoint.Y, args.Int(3) & 15, token));
        }

        private Value InvokePeekColor(
            CallArguments args, CancellationToken token)
        {
            int color;
            if (args.Int(3) != 0)
            {
                PixelImage clipboardImage = clipboard.ReadImage(token);
                token.ThrowIfCancellationRequested();
                int px = args.Int(0);
                int py = args.Int(1);
                if (px < 0 || py < 0 || px >= clipboardImage.Width || py >= clipboardImage.Height)
                {
                    return N(-1);
                }

                color = clipboardImage[px, py];
            }
            else
            {
                ImageSource source = GetImageSource();
                token.ThrowIfCancellationRequested();
                var pixelPosition = new Native.Point(
                    WindowsOperationErrors.Add(source.Origin.X, args.Int(0)),
                    WindowsOperationErrors.Add(source.Origin.Y, args.Int(1)));
                ImageArea bounds = source.Bounds;
                if (pixelPosition.X < bounds.X || pixelPosition.Y < bounds.Y
                    || (long)pixelPosition.X >= (long)bounds.X + bounds.Width
                    || (long)pixelPosition.Y >= (long)bounds.Y + bounds.Height)
                {
                    return N(-1);
                }

                color = CaptureImage(source,
                    new(pixelPosition.X, pixelPosition.Y, 1, 1), token)[0, 0];
            }

            token.ThrowIfCancellationRequested();
            return N(args.Int(2) switch
            {
                3 => color,
                4 => color >> 16 & 255,
                5 => color >> 8 & 255,
                6 => color & 255,
                _ => (color & 255) << 16 | (color & 0xff00) | (color >> 16 & 255)
            });
        }

    }
}
