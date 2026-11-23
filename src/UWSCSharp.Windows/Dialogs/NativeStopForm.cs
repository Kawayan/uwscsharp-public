using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDialogService
{
    private StopFormWindow? stopForm;
    private bool stopTopMost = true;

    internal Task<bool> GetStopTopMostAsync(CancellationToken token) => dispatcher.InvokeAsync(
        () => stopForm is null ? stopTopMost : WindowsStopFormLayout.IsTopMost(stopForm.Handle), token);

    internal Task SetStopTopMostAsync(bool topMost, CancellationToken token) => dispatcher.InvokeAsync(() =>
    {
        if (stopForm is not null)
        {
            WindowsStopFormLayout.SetTopMost(stopForm.Handle, topMost);
        }

        stopTopMost = topMost;
        return 0;
    }, token);

    internal Task<nint> GetStopFormHandleAsync(object owner, string scriptName,
        Action requestStop, (int Width, int Height)? size, CancellationToken token) => dispatcher.InvokeAsync(() =>
            EnsureStopForm(owner, scriptName, requestStop, size).Handle, token);

    internal Task SetStopFormAsync(object owner, string scriptName, Action requestStop,
        (int Width, int Height)? size, ScriptStopForm request, CancellationToken token) => dispatcher.InvokeAsync(() =>
        {
            StopFormWindow window = EnsureStopForm(owner, scriptName, requestStop, size);
            token.ThrowIfCancellationRequested();
            window.Apply(request);
            return 0;
        }, token);

    private StopFormWindow EnsureStopForm(object owner, string scriptName, Action requestStop,
        (int Width, int Height)? size)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
        }

        if (stopForm is null)
        {
            EnsureClass();
            var created = new StopFormWindow(this, owner, scriptName, requestStop, size);
            created.Create();
            stopForm = created;
        }

        return stopForm;
    }

    internal Task SetStopPositionAsync(object owner, string scriptName,
        Action requestStop, (int Width, int Height)? size,
        ScriptWindowPosition request, CancellationToken token) => dispatcher.InvokeAsync(() =>
        {
            StopFormWindow window = EnsureStopForm(owner, scriptName, requestStop, size);
            token.ThrowIfCancellationRequested();
            window.SetPosition(request);
            return 0;
        }, token);

    private sealed class StopFormWindow(NativeDialogService service, object owner,
        string scriptName, Action requestStop, (int Width, int Height)? size) : OwnedDialog
    {
        internal override object Owner => owner;
        private GCHandle self;
        private nint font;
        private nint button;
        private bool stopping;
        private ScriptWindowPosition? pendingPosition;

        internal void Create()
        {
            self = GCHandle.Alloc(this);
            try
            {
                Check(Native.CreateWindowExW(service.stopTopMost ? 0x88u : 0x80u,
                    service.className, "UWSC# 再生コントロール",
                    0x80c80000, 0, 0, 320, 140, 0, 0, service.instance,
                    GCHandle.ToIntPtr(self)) != 0);
                int Scale(int value) => checked((int)Math.Ceiling(value * GetDpiForWindow(Handle) / 96d));
                (int width, int height) = WindowsStopFormLayout.ConstrainSize(Handle,
                    size?.Width ?? Scale(320), size?.Height ?? Scale(140), Scale(320), Scale(140));
                Check(Native.SetWindowPos(Handle, 0, 0, 0, width, height, 0x16));
                font = CreateFontW(-Scale(14), 0, 0, 0, 400,
                    0, 0, 0, 1, 0, 0, 3, 0, "Segoe UI");
                Check(font != 0);
                if (!CoversStopCaption(font))
                {
                    Check(ImageNative.DeleteObject(font));
                    font = 0;
                    // 固定の日本語UIを表示できる書体をWindowsのマッパーへ要求します。
                    font = CreateFontW(-Scale(14), 0, 0, 0, 400,
                        0, 0, 0, 128, 0, 0, 3, 0, "");
                    Check(font != 0);
                    if (!CoversStopCaption(font))
                    {
                        throw new InvalidOperationException("停止ボタンの日本語を表示できる書体がありません。");
                    }
                }
                nint label = Native.CreateWindowExW(0,
                    ControlClassNames.Uppercase.Static, scriptName, 0x50000000,
                    Scale(12), Scale(10), Scale(280), Scale(24), Handle, 0, service.instance, 0);
                Check(label != 0);
                button = Native.CreateWindowExW(0,
                    ControlClassNames.Uppercase.Button, "停止", 0x50010001,
                    Scale(12), Scale(42), Scale(280), Scale(36), Handle, 1, service.instance, 0);
                Check(button != 0);
                Native.SendMessageW(label, 0x30, font, 1);
                Native.SendMessageW(button, 0x30, font, 1);
            }
            catch
            {
                if (Handle != 0)
                {
                    Native.DestroyWindow(Handle);
                }
                else if (self.IsAllocated)
                {
                    self.Free();
                }

                throw;
            }
        }

        internal void Apply(ScriptStopForm request)
        {
            if (!request.Visible)
            {
                Native.ShowWindow(Handle, 0);
                return;
            }

            Native.ShowWindow(Handle, 9);
            WindowsStopFormLayout.Apply(Handle, request.X, request.Y);
            if (!Native.SetForegroundWindow(Handle))
            {
                throw new InvalidOperationException("Windowsが停止画面の前面化を許可しませんでした。");
            }

            Native.SetFocus(button);
        }

        internal void SetPosition(ScriptWindowPosition request)
        {
            if (Native.IsIconic(Handle) || Native.IsZoomed(Handle))
            {
                // 状態を戻す操作は行わず、通常表示へ戻った時点で反映します。
                pendingPosition = request;
                return;
            }

            pendingPosition = null;
            WindowsStopFormLayout.Apply(Handle, request.X, request.Y);
        }

        private bool CoversStopCaption(nint selectedFont)
        {
            nint dc = ImageNative.GetDC(Handle);
            Check(dc != 0);
            nint previous = ImageNative.SelectObject(dc, selectedFont);
            try
            {
                ushort[] glyphs = new ushort[2];
                Check(StopFormGetGlyphIndicesW(dc, "停止", 2, glyphs, 1) == 2);
                return glyphs.All(glyph => glyph != ushort.MaxValue);
            }
            finally
            {
                ImageNative.SelectObject(dc, previous);
                ImageNative.ReleaseDC(Handle, dc);
            }
        }

        internal override nint Process(uint message, nint wParam, nint lParam)
        {
            if (message == 0x5 && wParam == 0
                && pendingPosition is { } position)
            {
                SetPosition(position);
            }

            if (message == 0x10 || (message == 0x111 && ((long)wParam & 0xffff) == 1))
            {
                if (!stopping)
                {
                    stopping = true;
                    StopFormEnableWindow(button, false);
                    requestStop();
                }

                return 0;
            }

            if (message == 0x82)
            {
                service.windows.Remove(Handle);
                service.stopForm = null;
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

            return Native.DefWindowProcW(Handle, message, wParam, lParam);
        }

        internal override void Cancel(CancellationToken token) => Check(Native.DestroyWindow(Handle));

        internal override void Fail(Exception error)
        {
            WindowsOperationErrors.Record(error);
            Cancel(CancellationToken.None);
        }
    }
}
