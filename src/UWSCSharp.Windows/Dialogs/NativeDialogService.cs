using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 実行内でUI threadを共有し、要求ごとの画面と完了を所有します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeDialogService : IAsyncDisposable
{
    private readonly NativeDialogDispatcher dispatcher = new();
    private readonly Dictionary<nint, OwnedDialog> windows = [];
    private readonly Action<Uri> openLink;
    private readonly Action<nint>? windowDestroyed;
    private readonly string className = "UWSCSharp.Dialog." + Guid.NewGuid().ToString("N");
    private readonly WindowProcedure procedure;
    private nint instance;
    private nint richEditModule;
    private bool registered;
    private (int X, int Y)? lastMessagePosition;
    private (int X, int Y)? lastInputPosition;
    private readonly object gate = new();
    private bool closing;
    private Task? disposal;

    internal NativeDialogService(Action<Uri>? openLink = null,
        Action<nint>? windowDestroyed = null)
    {
        this.openLink = openLink ?? OpenLink;
        this.windowDestroyed = windowDestroyed;
        procedure = WindowProc;
    }

    internal async Task<int> ShowMessageAsync(object owner, MessageBoxRequest request,
        DialogAppearance appearance, nint parent, CancellationToken token)
    {
        DialogWindow dialog = await ShowAsync(owner, request, null, appearance, parent, token)
            .ConfigureAwait(false);
        return await dialog.Completion.Task.ConfigureAwait(false);
    }

    internal async Task<Value> ShowInputAsync(object owner, InputBoxRequest request,
        DialogAppearance appearance, nint parent, CancellationToken token)
    {
        var message = new MessageBoxRequest(request.Message, 3, request.X, request.Y, 1, false);
        DialogWindow dialog = await ShowAsync(owner, message, request, appearance, parent, token)
            .ConfigureAwait(false);
        int result = await dialog.Completion.Task.ConfigureAwait(false);
        return result == 1 ? Value.String(dialog.InputResult) : Value.Empty;
    }

    private async Task<DialogWindow> ShowAsync(object owner, MessageBoxRequest request,
        InputBoxRequest? input, DialogAppearance appearance, nint parent, CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
        }

        DialogWindow dialog = await dispatcher.InvokeAsync(() =>
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
            }

            EnsureClass();
            var created = new DialogWindow(this, owner, request, input, appearance, parent, token);
            created.Create();
            return created;
        }, token).ConfigureAwait(false);
        using CancellationTokenRegistration registration = token.UnsafeRegister(_ =>
        {
            Task close = dispatcher.InvokeAsync(() =>
            {
                dialog.Cancel(token);
                return 0;
            });
            _ = close.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    dialog.Completion.TrySetException(task.Exception!.InnerExceptions);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }, null);
        await dialog.Completion.Task.ConfigureAwait(false);
        return dialog;
    }

    internal Task CloseOwnerAsync(object owner, CancellationToken token) =>
        dispatcher.InvokeAsync(() =>
        {
            foreach (OwnedDialog dialog in windows.Values.Where(value =>
                ReferenceEquals(value.Owner, owner)).ToArray())
            {
                dialog.Cancel(token);
            }

            return 0;
        });

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closing = true;
            disposal ??= DisposeCoreAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            Task[] menus = await dispatcher.InvokeAsync(() =>
            {
                PopupWindow[] active = [.. windows.Values.OfType<PopupWindow>()];
                foreach (PopupWindow popup in active)
                {
                    popup.Fail(new ObjectDisposedException(nameof(NativeDialogService)));
                }

                return (Task[])[.. active.Select(popup => popup.Finished.Task)];
            }).ConfigureAwait(false);
            await Task.WhenAll(menus).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() =>
            {
                foreach (OwnedDialog dialog in windows.Values.ToArray())
                {
                    dialog.Fail(new ObjectDisposedException(nameof(NativeDialogService)));
                }

                if (registered)
                {
                    Check(Native.UnregisterClassW(className, instance));
                    registered = false;
                }

                if (richEditModule != 0)
                {
                    Check(FreeLibrary(richEditModule));
                    richEditModule = 0;
                }

                return 0;
            }).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await dispatcher.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!dispatcher.IsAlive)
                {
                    balloonTextRuntime?.Dispose();
                    balloonTextRuntime = null;
                }
            }
        }
    }

    private void EnsureClass()
    {
        if (registered)
        {
            return;
        }

        instance = Native.GetModuleHandleW(null);
        if (richEditModule == 0)
        {
            richEditModule = LoadLibraryExW("msftedit.dll", 0, 0x800);
            Check(richEditModule != 0);
        }
        var definition = new Native.WindowClass
        {
            Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
            Instance = instance,
            Background = 16,
            Cursor = Native.LoadCursorW(0, 32512),
            ClassName = className
        };
        Check(Native.RegisterClassExW(ref definition) != 0);
        registered = true;
    }

    private nint WindowProc(nint handle, uint message, nint wParam, nint lParam)
    {
        OwnedDialog? dialog = null;
        try
        {
            if (message == 0x81)
            {
                dialog = (OwnedDialog?)GCHandle.FromIntPtr(Marshal.ReadIntPtr(lParam)).Target;
                if (dialog is not null)
                {
                    dialog.Handle = handle;
                    windows.Add(handle, dialog);
                }
            }
            else
            {
                windows.TryGetValue(handle, out dialog);
            }

            return dialog?.Process(message, wParam, lParam)
                ?? Native.DefWindowProcW(handle, message, wParam, lParam);
        }
        catch (Exception error)
        {
            // 画面イベントの失敗も、管理例外をNative境界へ出さず要求へ返します。
            dialog?.Fail(error);
            return 0;
        }
    }

    private static void OpenLink(Uri uri)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Win32Exception error)
        {
            throw WindowsOperationErrors.Record(error);
        }
    }

    private abstract class OwnedDialog
    {
        internal nint Handle
        {
            get; set;
        }

        internal abstract object Owner
        {
            get;
        }

        internal abstract nint Process(uint message, nint wParam, nint lParam);

        internal abstract void Cancel(CancellationToken token);

        internal abstract void Fail(Exception error);

        protected static int ScaleAtDpi(int value, uint dpi) =>
            checked((int)Math.Ceiling(value * (double)dpi / 96));

        protected static int FontHeightAtDpi(DialogAppearance appearance,
            uint dpi) =>
            checked((int)Math.Ceiling(appearance.FontSize * dpi / 72));

        protected static nint CreateDialogFont(int height, string family) =>
            CreateFontW(-Math.Max(1, height), 0, 0, 0, 400,
                0, 0, 0, 1, 0, 0, 5, 0, family);

        protected static nint CreateMessageControl(nint parent,
            nint instance, int padding, int width, int height) =>
            Native.CreateWindowExW(0, ControlClassNames.RichEdit50, "", 0x50210844,
                padding, padding, width, height, parent, 200, instance, 0);

        protected static void InitializeMessageControl(nint control,
            nint font)
        {
            Check(control != 0);
            Native.SendMessageW(control, 0x0459, 1, 0);
            Native.SendMessageW(control, 0x0435, 0, int.MaxValue);
            Native.SendMessageW(control, 0x30, font, 1);
            Native.SendMessageW(control, 0x0443, 0, (nint)GetSysColor(15));
        }
    }

    private sealed class DialogWindow(NativeDialogService service, object owner,
        MessageBoxRequest request, InputBoxRequest? inputRequest, DialogAppearance appearance,
        nint parent, CancellationToken token) : OwnedDialog
    {
        internal readonly TaskCompletionSource<int> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override object Owner => owner;
        private nint font;
        private nint text;
        private nint input;
        private bool hasReceivedFiles;
        private SubclassProcedure? inputProcedure;
        internal string InputResult = "";
        private readonly Dictionary<int, nint> buttons = [];
        private GCHandle self;
        private int result = 2;
        private int defaultId;
        private Exception? error;
        private CancellationToken? canceled;
        private bool initialized;
        private bool released;

        internal void Create()
        {
            try
            {
                self = GCHandle.Alloc(this);
                (int X, int Y)? previous = request.X == -1 && request.Y == -1
                    ? inputRequest is null ? service.lastMessagePosition : service.lastInputPosition : null;
                int? x = previous?.X ?? (request.X == -1 && request.Y == -1 ? null : request.X);
                int? y = previous?.Y ?? (request.X == -1 && request.Y == -1 ? null : request.Y);
                if (x == -999999 && y == -999999)
                {
                    x = y = null;
                }

                nint monitor = x.HasValue && y.HasValue
                    ? Native.MonitorFromPoint(new Native.Point { X = x.Value, Y = y.Value }, 2)
                    : Native.MonitorFromWindow(parent, 2);
                var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
                Check(Native.GetMonitorInfoW(monitor, ref info));
                Native.Rect work = info.Work;
                nint handle = Native.CreateWindowExW(1, service.className, appearance.Title,
                    0x80c80000, x ?? work.Left, y ?? work.Top, 100, 100,
                    parent, 0, service.instance, GCHandle.ToIntPtr(self));
                Check(handle != 0);
                uint dpi = GetDpiForWindow(handle);
                int Scale(int value) => ScaleAtDpi(value, dpi);
                int height = FontHeightAtDpi(appearance, dpi);
                int workWidth = work.Right - work.Left;
                int workHeight = work.Bottom - work.Top;
                if (height > workHeight / 3)
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException(
                        "指定フォントでは現在の画面内に操作ボタンを配置できません。"));
                }

                font = CreateDialogFont(height, appearance.FontFamily);
                Check(font != 0);
                int padding = Scale(12);
                int gap = Scale(8);
                int width = Math.Min(Math.Max(Scale(320), Scale(520)), workWidth - padding * 2);
                width = Math.Max(1, width);
                int buttonHeight = Math.Max(Scale(26), height + padding);
                int buttonWidth = Scale(80);
                nint dc = ImageNative.GetDC(handle);
                Check(dc != 0);
                nint saved = ImageNative.SelectObject(dc, font);
                int measuredBody = Scale(48);
                try
                {
                    var measure = new Native.Rect { Right = Math.Max(1, width - padding * 2) };
                    string displayText = request.Message.Replace("\0", "␀");
                    _ = DrawTextW(dc, displayText, displayText.Length, ref measure, 0x2c10);
                    double wrapped = Math.Ceiling((double)Math.Max(1, measure.Right) / (width - padding * 2));
                    measuredBody = (int)Math.Min(workHeight,
                        Math.Max(measure.Bottom + padding, wrapped * (height + Scale(2)) + padding));
                    foreach (int button in request.VisibleButtons)
                    {
                        string label = Caption(button).Replace("&", "");
                        Check(GetTextExtentPoint32W(dc, label, label.Length, out Size measured));
                        buttonWidth = Math.Max(buttonWidth, measured.Width + padding * 2);
                    }
                }
                finally
                {
                    ImageNative.SelectObject(dc, saved);
                    ImageNative.ReleaseDC(handle, dc);
                }

                int[] visible = [.. request.VisibleButtons];
                int columns = Math.Max(1, (width - padding * 2 + gap) / (buttonWidth + gap));
                int rows = (visible.Length + columns - 1) / columns;
                int inputHeight = inputRequest is null ? 0 : height + padding + gap;
                int panelHeight = rows * (buttonHeight + gap) + padding + inputHeight;
                int contentHeight = Math.Min(Math.Max(Scale(48), measuredBody), workHeight - panelHeight - Scale(80));
                if (contentHeight < height + padding || buttonWidth > width - padding * 2)
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException(
                        "指定フォントと画面サイズではダイアログを配置できません。"));
                }

                var bounds = new Native.Rect { Right = width, Bottom = contentHeight + panelHeight + padding };
                Check(AdjustWindowRectEx(ref bounds, 0x80c80000, false, 1));
                int outerWidth = bounds.Right - bounds.Left;
                int outerHeight = bounds.Bottom - bounds.Top;
                int left = x ?? work.Left + (workWidth - outerWidth) / 2;
                int top = y ?? work.Top + (workHeight - outerHeight) / 2;
                Check(Native.SetWindowPos(handle, 0, left, top, outerWidth, outerHeight, 0x14));

                text = CreateMessageControl(handle, service.instance,
                    padding, width - padding * 2, contentHeight);
                InitializeMessageControl(text, font);
                if (request.Links)
                {
                    Native.SendMessageW(text, 0x045b, 1, 0);
                    Native.SendMessageW(text, 0x0445, 0, 0x04000000);
                }

                // Nativeの終端文字で後半を黙って失わず、表示用の記号として残します。
                Check(SetWindowTextW(text, request.Message.Replace("\0", "␀")));
                if (inputRequest is not null)
                {
                    input = Native.CreateWindowExW(0x200,
                        ControlClassNames.Uppercase.Edit, "", 0x50810080u
                        | (inputRequest.Password ? 0x20u : 0u), padding,
                        padding + contentHeight + gap, width - padding * 2,
                        height + padding, handle, 201, service.instance, 0);
                    Check(input != 0);
                    Native.SendMessageW(input, 0xc5, int.MaxValue - 1, 0);
                    Native.SendMessageW(input, 0x30, font, 1);
                    Check(SetWindowTextW(input, inputRequest.DefaultText));
                    inputProcedure = InputProc;
                    Check(SetWindowSubclass(input, inputProcedure, 1, 0));
                    DragAcceptFiles(input, true);
                    DragAcceptFiles(handle, true);
                }

                for (int index = 0; index < visible.Length; index++)
                {
                    int value = visible[index];
                    int id = 4096 + value;
                    bool isDefault = value == request.DefaultButton;
                    nint button = Native.CreateWindowExW(0,
                        ControlClassNames.Uppercase.Button, Caption(value),
                        0x50010000u | (isDefault ? 1u : 0u),
                        padding + index % columns * (buttonWidth + gap),
                        padding + contentHeight + inputHeight + gap + index / columns * (buttonHeight + gap),
                        buttonWidth, buttonHeight, handle, id, service.instance, 0);
                    Check(button != 0);
                    buttons.Add(id, button);
                    Native.SendMessageW(button, 0x30, font, 1);
                    if (isDefault)
                    {
                        defaultId = id;
                    }
                }

                service.dispatcher.RegisterDialog(handle, Fail);
                initialized = true;
                token.ThrowIfCancellationRequested();
                Native.ShowWindow(handle, 5);
                Native.SetForegroundWindow(handle);
                Native.SetFocus(input != 0 ? input : buttons[defaultId]);
                if (input != 0)
                {
                    Native.SendMessageW(input, 0xb1, 0, -1);
                }
            }
            catch (Exception failure)
            {
                error = failure;
                if (Handle != 0)
                {
                    Native.DestroyWindow(Handle);
                }
                else
                {
                    Release();
                }

                // 生成失敗は直接呼出元へ返し、未受渡しの完了Taskも観測します。
                _ = Completion.Task.Exception;
                throw;
            }
        }

        internal override nint Process(uint message, nint wParam, nint lParam)
        {
            if (message == 0x233 && input != 0)
            {
                ReceiveFiles(wParam);
                return 0;
            }

            if (message == 0x10)
            {
                Close(2);
                return 0;
            }

            if (message == 0x111)
            {
                int id = (int)((long)wParam & 0xffff);
                if (buttons.ContainsKey(id))
                {
                    Close(id - 4096);
                    return 0;
                }

                if (id == 1)
                {
                    Close(defaultId - 4096);
                    return 0;
                }

                if (id == 2)
                {
                    Close(2);
                    return 0;
                }
            }

            if (message == 0x400)
            {
                return (0x534b << 16) | defaultId;
            }

            if (message == 0x401 && buttons.ContainsKey((int)wParam))
            {
                Native.SendMessageW(buttons[defaultId], 0xf4, 0, 1);
                defaultId = (int)wParam;
                Native.SendMessageW(buttons[defaultId], 0xf4, 1, 1);
                return 0;
            }

            if (message == 0x4e && request.Links && lParam != 0)
            {
                LinkNotification link = Marshal.PtrToStructure<LinkNotification>(lParam);
                if (link.Header.Window == text && link.Header.Code == 0x70b && link.Message == 0x202)
                {
                    ActivateLink(link.Start, link.End);
                    return 0;
                }
            }

            if (message == 0x82)
            {
                Release();
            }

            return Native.DefWindowProcW(Handle, message, wParam, lParam);
        }

        private void ActivateLink(int start, int end)
        {
            if (start < 0 || end <= start || end - start > request.Message.Length)
            {
                return;
            }

            nint buffer = Marshal.AllocHGlobal(checked((end - start + 1) * 2));
            try
            {
                var range = new TextRange { Start = start, End = end, Text = buffer };
                nint structure = Marshal.AllocHGlobal(Marshal.SizeOf<TextRange>());
                try
                {
                    Marshal.StructureToPtr(range, structure, false);
                    int length = (int)Native.SendMessageW(text, 0x044b, 0, structure);
                    string value = Marshal.PtrToStringUni(buffer, Math.Max(0, Math.Min(length, end - start))) ?? "";
                    if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                        && uri.Scheme is "http" or "https" or "mailto")
                    {
                        service.openLink(uri);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(structure);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private nint InputProc(nint window, uint message, nint wParam, nint lParam,
            nuint subclass, nuint reference)
        {
            try
            {
                if (message == 0x233)
                {
                    ReceiveFiles(wParam);
                    return 0;
                }

                if (message == 0x82)
                {
                    _ = RemoveWindowSubclass(window, inputProcedure!, subclass);
                }

                return DefSubclassProc(window, message, wParam, lParam);
            }
            catch (Exception failure)
            {
                Fail(failure);
                return 0;
            }
        }

        private void ReceiveFiles(nint drop)
        {
            try
            {
                uint count = DragQueryFileW(drop, uint.MaxValue, null, 0);
                List<string> paths = [];
                for (uint index = 0; index < count; index++)
                {
                    uint length = DragQueryFileW(drop, index, null, 0);
                    var path = new StringBuilder(checked((int)length + 1));
                    Check(DragQueryFileW(drop, index, path, (uint)path.Capacity) == length);
                    paths.Add(path.ToString());
                }

                if (paths.Count > 0)
                {
                    string text = string.Join("\t", paths);
                    if (!hasReceivedFiles)
                    {
                        text = ReadInputText() + text;
                    }

                    Check(SetWindowTextW(input, text));
                    hasReceivedFiles = true;
                    Native.SetFocus(input);
                    Native.SendMessageW(input, 0xb1, 0, -1);
                }
            }
            finally
            {
                DragFinish(drop);
            }
        }

        private string ReadInputText()
        {
            int length = Native.GetWindowTextLengthW(input);
            var text = new StringBuilder(checked(length + 1));
            Native.GetWindowTextW(input, text, text.Capacity);
            return text.ToString();
        }

        internal override void Cancel(CancellationToken cancellationToken)
        {
            canceled = cancellationToken;
            Close(2);
        }

        internal override void Fail(Exception failure)
        {
            error = failure;
            Close(2);
        }

        private void Close(int value)
        {
            if (released)
            {
                return;
            }

            result = value;
            if (value == 1 && input != 0 && error is null && canceled is null)
            {
                InputResult = ReadInputText();
            }

            if (Native.GetWindowRect(Handle, out Native.Rect rect))
            {
                if (inputRequest is null)
                {
                    service.lastMessagePosition = (rect.Left, rect.Top);
                }
                else
                {
                    service.lastInputPosition = (rect.Left, rect.Top);
                }
            }

            if (!Native.DestroyWindow(Handle))
            {
                error ??= NativeError();
                Completion.TrySetException(error);
            }
        }

        private void Release()
        {
            if (released)
            {
                return;
            }

            released = true;
            service.windows.Remove(Handle);
            if (initialized)
            {
                service.dispatcher.RemoveDialog(Handle);
            }

            if (font != 0)
            {
                if (!ImageNative.DeleteObject(font))
                {
                    error ??= NativeError();
                }

                font = 0;
            }

            if (self.IsAllocated)
            {
                self.Free();
            }

            if (error is not null)
            {
                Completion.TrySetException(error);
            }
            else if (token.IsCancellationRequested || canceled.HasValue)
            {
                Completion.TrySetCanceled(token.IsCancellationRequested ? token : canceled!.Value);
            }
            else
            {
                Completion.TrySetResult(result);
            }
        }

        private static string Caption(int button) => button switch
        {
            1 => "OK",
            2 => "Cancel",
            4 => "はい(&Y)",
            8 => "いいえ(&N)",
            16 => "中止(&A)",
            32 => "再試行(&R)",
            64 => "無視(&I)",
            _ => throw new InvalidOperationException("未対応のボタンです。")
        };
    }

    private static Exception NativeError() => WindowsOperationErrors.Record(
        new Win32Exception(Marshal.GetLastWin32Error()));

    private static void Check(bool success)
    {
        if (!success)
        {
            throw NativeError();
        }
    }

    private delegate nint SubclassProcedure(nint window, uint message, nint wParam,
        nint lParam, nuint subclass, nuint reference);

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        internal int Width;
        internal int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        internal nint Window;
        internal nuint Id;
        internal uint Code;
    }

    // Richedit.hのENLINKは4バイト境界、内包するNMHDRは通常の境界を使います。
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LinkNotification
    {
        internal Header Header;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal int Start;
        internal int End;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextRange
    {
        internal int Start;
        internal int End;
        internal nint Text;
    }
}
