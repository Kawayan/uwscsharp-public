namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private readonly record struct ImageSource(
            nint Window, ImageArea Bounds, Native.Point Origin,
            Native.Point WindowOrigin);

        private nint RequireMouseOrigin()
        {
            if (!Native.IsWindow(originWindow))
            {
                throw WindowsOperationErrors.Record(new InvalidOperationException(
                    "MOUSEORGのウィンドウが無効です。"));
            }

            return originWindow;
        }

        private ImageSource GetImageSource()
        {
            using var dpi = new PhysicalDpiScope();
            if (!hasMouseOrigin)
            {
                return new(0, capture.Desktop, default, default);
            }

            nint window = RequireMouseOrigin();
            Native.Rect outer = Rect(window);
            Native.Rect bounds = clientOrigin ? Rect(window, true) : outer;
            var origin = new Native.Point(bounds.Left, bounds.Top);
            if (clientOrigin)
            {
                Check(Native.ClientToScreen(window, ref origin));
            }

            var area = new ImageArea(origin.X, origin.Y,
                WindowsOperationErrors.Subtract(bounds.Right, bounds.Left),
                WindowsOperationErrors.Subtract(bounds.Bottom, bounds.Top));
            if (!backgroundCapture)
            {
                ImageArea desktop = capture.Desktop;
                long left = Math.Max(area.X, desktop.X);
                long top = Math.Max(area.Y, desktop.Y);
                long right = Math.Min((long)area.X + area.Width,
                    (long)desktop.X + desktop.Width);
                long bottom = Math.Min((long)area.Y + area.Height,
                    (long)desktop.Y + desktop.Height);
                return new(0, new(WindowsOperationErrors.Int32(left),
                    WindowsOperationErrors.Int32(top),
                    WindowsOperationErrors.Int32(Math.Max(0, right - left)),
                    WindowsOperationErrors.Int32(Math.Max(0, bottom - top))),
                    origin, default);
            }

            return new(window, area,
                origin, new(outer.Left, outer.Top));
        }

        private PixelImage CaptureImage(ImageSource source,
            ImageArea area, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (source.Window == 0)
            {
                using var dpi = new PhysicalDpiScope();
                return capture.Capture(area, token);
            }

            if (capture is not IWindowImageCapture background)
            {
                throw WindowsOperationErrors.Record(new NotSupportedException(
                    "画像取得バックエンドはウィンドウの背景取得に対応していません。"));
            }

            return background.CaptureWindow(source.Window,
                new(WindowsOperationErrors.Subtract(area.X, source.WindowOrigin.X),
                    WindowsOperationErrors.Subtract(area.Y, source.WindowOrigin.Y),
                    area.Width, area.Height), token);
        }
    }
}
