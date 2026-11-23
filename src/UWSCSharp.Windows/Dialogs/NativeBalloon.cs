using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDialogService
{
    private readonly Dictionary<object, BalloonWindow> balloons = new(ReferenceEqualityComparer.Instance);
    private BalloonTextLayout.Runtime? balloonTextRuntime;

    internal Task<int> GetBalloonIdAsync(object owner, Func<nint, int> register,
        CancellationToken token) => dispatcher.InvokeAsync(() =>
            balloons.TryGetValue(owner, out BalloonWindow? window)
                ? register(window.Handle) : -1, token);

    internal Task<Value> ShowBalloonAsync(object owner, BalloonRequest request,
        DialogAppearance appearance, CancellationToken ownerStop,
        CancellationToken runStop, CancellationToken token) => dispatcher.InvokeAsync(() =>
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
            }

            ownerStop.ThrowIfCancellationRequested();
            runStop.ThrowIfCancellationRequested();
            if (!balloons.TryGetValue(owner, out BalloonWindow? window))
            {
                if (request.Hidden)
                {
                    return Value.String("");
                }

                EnsureClass();
                balloonTextRuntime ??= new BalloonTextLayout.Runtime();
                window = new BalloonWindow(this, owner);
                window.Create();
                balloons.Add(owner, window);
                window.ObserveStop(ownerStop, runStop);
            }

            try
            {
                window.Update(request, appearance, token);
                token.ThrowIfCancellationRequested();
                return Value.String("");
            }
            catch
            {
                window.Cancel(token);
                throw;
            }
        }, token);

    private sealed class BalloonWindow(NativeDialogService service, object owner) : OwnedDialog
    {
        internal override object Owner => owner;
        private GCHandle self;
        private CancellationTokenRegistration ownerRegistration;
        private CancellationTokenRegistration runRegistration;
        private nint font;
        private BalloonTextLayout? textLayout;
        private Native.Point[] outline = [];
        private Native.Rect body;
        private Native.Rect textBounds;
        private int textColor;
        private int background;
        private int frameColor;
        private int transparency;
        private int lineHeight;
        private int contentHeight;
        private int scroll;
        private int padding;
        private int x;
        private int y;
        private bool released;
        private bool fixedDesktop;

        internal void Create()
        {
            self = GCHandle.Alloc(this);
            try
            {
                // 入力先を切り替えず、タスクバーにも追加しません。
                Check(Native.CreateWindowExW(0x08080088, service.className, "",
                    0x80000000, 0, 0, 1, 1, 0, 0, service.instance,
                    GCHandle.ToIntPtr(self)) != 0);
            }
            catch
            {
                if (Handle != 0)
                {
                    Native.DestroyWindow(Handle);
                }

                Release();
                throw;
            }
        }

        internal void ObserveStop(CancellationToken ownerStop, CancellationToken runStop)
        {
            ownerRegistration = Observe(ownerStop);
            runRegistration = Observe(runStop);

            CancellationTokenRegistration Observe(CancellationToken stop) => stop.UnsafeRegister(_ =>
            {
                Task close = service.dispatcher.InvokeAsync(() =>
                {
                    Cancel(stop);
                    return 0;
                });
                _ = close.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        WindowsOperationErrors.Record(task.Exception!.GetBaseException());
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }, null);
        }

        internal void Update(BalloonRequest request, DialogAppearance appearance, CancellationToken token)
        {
            if (request.Hidden)
            {
                Native.ShowWindow(Handle, 0);
                return;
            }

            if (appearance.FixedBalloon && !fixedDesktop)
            {
                AttachToDesktop();
            }

            x = request.X ?? x;
            y = request.Y ?? y;
            nint monitor = Native.MonitorFromPoint(new Native.Point(x, y), 2);
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            Check(Native.GetMonitorInfoW(monitor, ref info));
            Native.Rect work = info.Work;
            uint dpi = GetDpiForWindow(Handle);
            int Scale(int value) => checked((int)Math.Ceiling(value * (double)dpi / 96));
            int height = checked((int)Math.Ceiling((request.FontSize ?? appearance.FontSize) * dpi / 72));
            int workWidth = work.Right - work.Left;
            int workHeight = work.Bottom - work.Top;
            if (height > workHeight / 6 || workWidth < Scale(120))
            {
                throw new InvalidOperationException("指定フォントと作業領域では吹出しを配置できません。");
            }

            nint nextFont = CreateFontW(-Math.Max(1, height), 0, 0, 0, 400,
                0, 0, 0, 1, 0, 0, 3, 0, request.FontFamily ?? appearance.FontFamily);
            Check(nextFont != 0);
            if (font != 0 && !ImageNative.DeleteObject(font))
            {
                Exception error = NativeError();
                ImageNative.DeleteObject(nextFont);
                throw error;
            }

            font = nextFont;
            textColor = request.TextColor ?? (int)GetSysColor(23);
            background = request.BackgroundColor is null or 0
                ? (int)GetSysColor(24) : request.BackgroundColor.Value;
            transparency = request.Transparency ?? 0;
            frameColor = request.BackgroundColor ?? 0;
            if (transparency == -1 && background == frameColor)
            {
                background ^= 1;
            }

            if (transparency < 0 && background == textColor)
            {
                background ^= 2;
            }
            lineHeight = Math.Max(1, height);
            padding = Scale(10);
            int direction = request.Direction ?? 0;
            int tail = direction == 0 ? 0 : Scale(12);
            int maxWidth = Math.Min(Scale(600), workWidth - padding * 2 - tail);
            int maxHeight = workHeight - padding * 4 - tail;
            nint dc = ImageNative.GetDC(Handle);
            Check(dc != 0);
            int width;
            int bodyHeight;
            try
            {
                nint previous = ImageNative.SelectObject(dc, font);
                try
                {
                    textLayout = BalloonTextLayout.Create(dc, request.Message,
                        maxWidth - padding * 4, token);
                    lineHeight = textLayout.LineHeight;
                    if (lineHeight > workHeight / 6)
                    {
                        throw new InvalidOperationException(
                            "代替書体の行高を吹出しの作業領域に収められません。");
                    }

                    width = Math.Min(maxWidth, Math.Max(Scale(80),
                        textLayout.Width + padding * 4));
                    contentHeight = textLayout.Height;
                    bodyHeight = Math.Min(maxHeight, Math.Max(lineHeight, contentHeight) + padding * 2);
                }
                finally
                {
                    ImageNative.SelectObject(dc, previous);
                }
            }
            finally
            {
                ImageNative.ReleaseDC(Handle, dc);
            }

            int outerWidth = width + (direction is 3 or 4 ? tail : 0);
            int outerHeight = bodyHeight + (direction is 1 or 2 ? tail : 0);
            int left = direction == 3 ? tail : 0;
            int top = direction == 1 ? tail : 0;
            body = new Native.Rect { Left = left, Top = top, Right = left + width - 1, Bottom = top + bodyHeight - 1 };
            textBounds = new Native.Rect
            {
                Left = left + padding,
                Top = top + padding,
                Right = left + width - padding * 3,
                Bottom = top + bodyHeight - padding
            };
            scroll = 0;
            outline = Outline(body, direction, tail);
            nint region = BalloonCreatePolygonRgn(outline, outline.Length, 1);
            Check(region != 0);
            if (BalloonSetWindowRgn(Handle, region, true) == 0)
            {
                ImageNative.DeleteObject(region);
                throw NativeError();
            }

            // 領域の所有権は成功したSetWindowRgnへ移っています。
            Check(Native.SetWindowPos(Handle, -1,
                (int)Math.Clamp((long)x, work.Left, (long)work.Right - outerWidth),
                (int)Math.Clamp((long)y, work.Top, (long)work.Bottom - outerHeight),
                outerWidth, outerHeight, 0x10));
            Check(BalloonSetLayeredWindowAttributes(Handle, (uint)background,
                request.Alpha ?? 255, transparency < 0 ? 1u : 2u));
            Check(SetWindowTextW(Handle, request.Message));
            Check(BalloonInvalidateRect(Handle, 0, false));
            Native.ShowWindow(Handle, 4);
        }

        private void AttachToDesktop()
        {
            // 原版と同じくデスクトップ直下の子にし、入力先は変更しません。
            int style = Native.GetWindowLongW(Handle, -16);
            Marshal.SetLastPInvokeError(0);
            int previous = BalloonSetWindowLongW(Handle, -16,
                (style & ~unchecked((int)0x80000000)) | 0x40000000);
            Check(previous != 0 || Marshal.GetLastPInvokeError() == 0);
            nint desktop = BalloonGetDesktopWindow();
            Check(desktop != 0);
            Marshal.SetLastPInvokeError(0);
            nint parent = BalloonSetParent(Handle, desktop);
            Check(parent != 0 || Marshal.GetLastPInvokeError() == 0);
            Check(Native.SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x37));
            fixedDesktop = true;
        }

        private static Native.Point[] Outline(Native.Rect r, int direction, int tail)
        {
            int centerX = (r.Left + r.Right) / 2;
            int centerY = (r.Top + r.Bottom) / 2;
            int spanX = Math.Min(tail, (r.Right - r.Left) / 4);
            int spanY = Math.Min(tail, (r.Bottom - r.Top) / 4);
            List<Native.Point> points = [new(r.Left, r.Top)];
            if (direction == 1)
            {
                points.AddRange([new(centerX - spanX, r.Top), new(centerX, 0), new(centerX + spanX, r.Top)]);
            }

            points.Add(new(r.Right, r.Top));
            if (direction == 4)
            {
                points.AddRange([new(r.Right, centerY - spanY), new(r.Right + tail, centerY), new(r.Right, centerY + spanY)]);
            }

            points.Add(new(r.Right, r.Bottom));
            if (direction == 2)
            {
                points.AddRange([new(centerX + spanX, r.Bottom), new(centerX, r.Bottom + tail), new(centerX - spanX, r.Bottom)]);
            }

            points.Add(new(r.Left, r.Bottom));
            if (direction == 3)
            {
                points.AddRange([new(r.Left, centerY + spanY), new(0, centerY), new(r.Left, centerY - spanY)]);
            }

            return [.. points];
        }

        internal override nint Process(uint messageId, nint wParam, nint lParam)
        {
            switch (messageId)
            {
                case 0x21:
                    return 3;
                case 0x14:
                    return 1;
                case 0xf:
                    Paint();
                    return 0;
                case 0x20a:
                    MoveScroll(-((short)((long)wParam >> 16)) / 120 * lineHeight * 3);
                    return 0;
                case 0x201:
                    int clickX = (short)((long)lParam & 0xffff);
                    int clickY = (short)((long)lParam >> 16);
                    if (clickX >= textBounds.Right)
                    {
                        MoveScroll(clickY < (body.Top + body.Bottom) / 2 ? -lineHeight * 3 : lineHeight * 3);
                    }

                    return 0;
                case 0x10:
                    Native.ShowWindow(Handle, 0);
                    return 0;
                case 0x82:
                    Release();
                    break;
            }

            return Native.DefWindowProcW(Handle, messageId, wParam, lParam);
        }

        private void MoveScroll(int amount)
        {
            scroll = (int)Math.Clamp((long)scroll + amount, 0,
                Math.Max(0, contentHeight - (textBounds.Bottom - textBounds.Top)));
            BalloonInvalidateRect(Handle, 0, false);
        }

        private void Paint()
        {
            nint dc = BalloonBeginPaint(Handle, out BalloonPaint paint);
            try
            {
                if (outline.Length == 0 || font == 0 || textLayout is null)
                {
                    return;
                }

                nint brush = BalloonCreateSolidBrush((uint)background);
                Check(brush != 0);
                nint oldBrush = ImageNative.SelectObject(dc, brush);
                int penIndex = transparency switch
                {
                    -2 => 8,
                    -1 => 19,
                    _ => 7
                };
                nint oldPen = ImageNative.SelectObject(dc,
                    BalloonGetStockObject(penIndex));
                nint oldFont = ImageNative.SelectObject(dc, font);
                try
                {
                    if (transparency == -1)
                    {
                        Check(BalloonSetDCPenColor(dc, (uint)frameColor)
                            != uint.MaxValue);
                    }

                    Check(BalloonPolygon(dc, outline, outline.Length));
                    BalloonSetBkMode(dc, 1);
                    BalloonSetTextColor(dc, (uint)textColor);
                    int saved = BalloonSaveDC(dc);
                    BalloonIntersectClipRect(dc, textBounds.Left, textBounds.Top, textBounds.Right, textBounds.Bottom);
                    textLayout.Draw(dc, textBounds.Left, textBounds.Top,
                        scroll, textBounds.Right - textBounds.Left,
                        textBounds.Bottom - textBounds.Top, textColor);
                    BalloonRestoreDC(dc, saved);
                    if (contentHeight > textBounds.Bottom - textBounds.Top)
                    {
                        var controls = new Native.Rect { Left = textBounds.Right, Top = body.Top + padding, Right = body.Right, Bottom = body.Bottom - padding };
                        DrawTextW(dc, "▲", 1, ref controls, 0x801);
                        controls.Top = Math.Max(controls.Top, controls.Bottom - lineHeight);
                        DrawTextW(dc, "▼", 1, ref controls, 0x801);
                    }
                }
                finally
                {
                    ImageNative.SelectObject(dc, oldFont);
                    ImageNative.SelectObject(dc, oldPen);
                    ImageNative.SelectObject(dc, oldBrush);
                    ImageNative.DeleteObject(brush);
                }
            }
            finally
            {
                BalloonEndPaint(Handle, ref paint);
            }
        }

        internal override void Cancel(CancellationToken token)
        {
            if (!released)
            {
                Check(Native.DestroyWindow(Handle));
            }
        }

        internal override void Fail(Exception error)
        {
            WindowsOperationErrors.Record(error);
            Cancel(CancellationToken.None);
        }

        private void Release()
        {
            if (released)
            {
                return;
            }

            released = true;
            ownerRegistration.Unregister();
            runRegistration.Unregister();
            service.windows.Remove(Handle);
            service.balloons.Remove(owner);
            service.windowDestroyed?.Invoke(Handle);
            if (font != 0)
            {
                ImageNative.DeleteObject(font);
                font = 0;
            }

            if (self.IsAllocated)
            {
                self.Free();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BalloonPaint
    {
        public nint Dc;
        public int Erase;
        public Native.Rect Bounds;
        public int Restore;
        public int IncrementalUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }
}
