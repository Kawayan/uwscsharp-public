using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private void SaveImage(string path, int id, int? requestedX,
            int? requestedY, int width, int height, bool client,
            int quality, int mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var dpi = new PhysicalDpiScope();
            nint window = 0;
            Native.Point windowOrigin = default;
            ImageArea bounds;
            ImageArea desktop = default;
            if (id == 0 || mode != 2)
            {
                desktop = capture.Desktop;
                token.ThrowIfCancellationRequested();
            }

            if (id == 0)
            {
                bounds = desktop;
            }
            else
            {
                window = Resolve(id, false);
                if (window == 0)
                {
                    throw ScriptError(BuiltinFunctionNames.SaveImg,
                        new InvalidOperationException("SAVEIMGのウィンドウが無効です。"));
                }

                Native.Rect outer = Rect(window);
                windowOrigin = new(outer.Left, outer.Top);
                Native.Rect selected = client ? Rect(window, true) : outer;
                var start = new Native.Point(selected.Left, selected.Top);
                if (client)
                {
                    Check(Native.ClientToScreen(window, ref start));
                }

                bounds = new(start.X, start.Y,
                    WindowsOperationErrors.Subtract(selected.Right, selected.Left),
                    WindowsOperationErrors.Subtract(selected.Bottom, selected.Top));
            }

            token.ThrowIfCancellationRequested();
            int x = id == 0
                ? WindowsOperationErrors.Subtract(requestedX ?? bounds.X, bounds.X)
                : requestedX ?? 0;
            int y = id == 0
                ? WindowsOperationErrors.Subtract(requestedY ?? bounds.Y, bounds.Y)
                : requestedY ?? 0;
            width = width == 0
                ? WindowsOperationErrors.Subtract(bounds.Width, x) : width;
            height = height == 0
                ? WindowsOperationErrors.Subtract(bounds.Height, y) : height;
            var area = new ImageArea(
                WindowsOperationErrors.Add(bounds.X, x),
                WindowsOperationErrors.Add(bounds.Y, y), width, height);
            if (width < 1 || height < 1 || x < 0 || y < 0
                || (long)x + width > bounds.Width
                || (long)y + height > bounds.Height)
            {
                throw ScriptError(BuiltinFunctionNames.SaveImg,
                    new ArgumentException("SAVEIMGの取得範囲が範囲外です。"));
            }

            bool background = window != 0 && (mode == 2
                || (mode == 0 && !SaveAreaIsVisible(window, area, desktop, token)));
            if (!background && !ContainsArea(desktop, area))
            {
                throw ScriptError(BuiltinFunctionNames.SaveImg,
                    new ArgumentException("SAVEIMGの可視取得範囲が画面外です。"));
            }

            var source = new ImageSource(background ? window : 0,
                bounds, default, windowOrigin);
            PixelImage image = CaptureImage(source, area, token);
            token.ThrowIfCancellationRequested();
            if (path.Length == 0)
            {
                if (quality >= 0)
                {
                    clipboard.WriteImage(image, token);
                }
                else if (clipboard is IClipboardImageFormats formatted)
                {
                    formatted.WriteImage(image, -quality, token);
                }
                else
                {
                    throw WindowsOperationErrors.Record(new NotSupportedException(
                        "クリップボードのバックエンドは色深度指定に対応していません。"));
                }
            }
            else
            {
                string destination = ImageSavePath.Resolve(path, quality > 0);
                if (quality <= 0)
                {
                    BitmapFileWriter.Save(image, destination,
                        quality == 0 ? 24 : -quality, token);
                }
                else
                {
                    JpegWriter.Save(image, destination, quality, token);
                }
            }
        }

        private static bool ContainsArea(ImageArea bounds, ImageArea area) =>
            area.X >= bounds.X && area.Y >= bounds.Y
            && (long)area.X + area.Width <= (long)bounds.X + bounds.Width
            && (long)area.Y + area.Height <= (long)bounds.Y + bounds.Height;

        private static bool SaveAreaIsVisible(nint window, ImageArea area,
            ImageArea desktop, CancellationToken token)
        {
            if (!ContainsArea(desktop, area) || !Native.IsWindowVisible(window)
                || Native.IsIconic(window))
            {
                return false;
            }

            nint root = Native.GetAncestor(window, 2);
            var parents = new HashSet<nint>();
            for (nint current = window; current != 0;
                current = Native.GetAncestor(current, 1))
            {
                token.ThrowIfCancellationRequested();
                if (!parents.Add(current))
                {
                    return false;
                }

                var siblings = new HashSet<nint>();
                for (nint above = Native.GetWindow(current, 3); above != 0;
                    above = Native.GetWindow(above, 3))
                {
                    token.ThrowIfCancellationRequested();
                    if (!siblings.Add(above))
                    {
                        return false;
                    }

                    if (!Native.IsWindowVisible(above) || Native.IsIconic(above))
                    {
                        continue;
                    }

                    // 競合で矩形を読めない場合も、背景取得へ寄せます。
                    if (!Native.GetWindowRect(above, out Native.Rect rect))
                    {
                        return false;
                    }

                    if (rect.Left < (long)area.X + area.Width
                        && rect.Right > area.X
                        && rect.Top < (long)area.Y + area.Height
                        && rect.Bottom > area.Y)
                    {
                        return false;
                    }
                }

                if (current == root)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
