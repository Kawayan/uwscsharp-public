using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private const double DefaultWindowLookupSeconds = 0.1;
        private const int WindowLookupPollMilliseconds = 10;

        private Value InvokeHndToId(CallArguments args)
        {
            long handleValue = WindowsOperationErrors.Calculate(() => checked((long)Math.Truncate(
                NumericOperations.Number(args.Values[0]))));
            return N(Register(WindowsOperationErrors.Calculate(() => checked((nint)handleValue))));
        }

        private async ValueTask<Value> InvokeGetIdAsync(
            CallArguments args, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            await WaitForPauseAsync(watch, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string title = args.Str(0);
            string className = args.Str(1);
            string childTitle = args.Str(3);
            double seconds = args.Has(2)
                ? args.Values[2].AsNumber() : 0;
            string selector = title.ToUpperInvariant();
            if (selector == BuiltinWindowSelectors.GetFukidasiWin)
            {
                return N(await resources.GetBalloonIdAsync(this, token).ConfigureAwait(false));
            }

            if (selector == BuiltinWindowSelectors.GetScheduleWin
                && capturedScheduleWindow is { } captured)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                return N(resources.RegisterCaptured(captured));
            }

            WindowsHostWindow? hostWindow = selector switch
            {
                BuiltinWindowSelectors.GetThisUwscWin => WindowsHostWindow.Main,
                BuiltinWindowSelectors.GetLogPrintWin => WindowsHostWindow.Log,
                BuiltinWindowSelectors.GetFormWin => WindowsHostWindow.Form,
                BuiltinWindowSelectors.GetFormWin2 => WindowsHostWindow.Form2,
                BuiltinWindowSelectors.GetScheduleWin => WindowsHostWindow.Schedule,
                BuiltinWindowSelectors.GetStopFormWin => WindowsHostWindow.Stop,
                _ => null
            };
            if (hostWindow is WindowsHostWindow kind)
            {
                nint handle = hostWindowHandle is null ? 0
                    : await hostWindowHandle(kind, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                return N(Register(Native.IsWindow(handle) ? handle : 0));
            }

            if (selector == BuiltinWindowSelectors.GetActiveWin)
            {
                return N(Register(
                    lookup.ForegroundWindow()));
            }

            if (selector is BuiltinWindowSelectors.GetFrompointWin or BuiltinWindowSelectors.GetFrompointObj)
            {
                Native.Point point = lookup.CursorPosition();
                nint handle = lookup.WindowAt(point);
                return N(Register(selector
                    == BuiltinWindowSelectors.GetFrompointWin ? lookup.RootOf(handle)
                    : handle));
            }

            do
            {
                token.ThrowIfCancellationRequested();
                await WaitForPauseAsync(watch, token).ConfigureAwait(false);
                nint exact = 0;
                nint partial = 0;
                Native.EnumWindows((handle, _) =>
                {
                    string caption = Text(handle);
                    string cls = Text(handle, true);
                    if (className.Length > 0
                        && !cls.StartsWith(className,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (caption.Equals(title,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        exact = handle;
                        return false;
                    }

                    if (partial == 0
                        && caption.Contains(title, StringComparison.OrdinalIgnoreCase))
                    {
                        partial = handle;
                    }

                    return true;
                }, 0);
                if (exact != 0 || partial != 0)
                {
                    nint parent = exact != 0 ? exact : partial;
                    if (childTitle.Length == 0)
                    {
                        return N(Register(parent));
                    }

                    nint child = FindGetIdChild(parent, childTitle);
                    if (child != 0)
                    {
                        return N(Register(child));
                    }
                }

                if (seconds >= 0
                    && watch.Elapsed.TotalSeconds >= (seconds == 0
                        ? DefaultWindowLookupSeconds : seconds))
                {
                    return N(-1);
                }

                await Task.Delay(WindowLookupPollMilliseconds,
                    token).ConfigureAwait(false);
            }
            while (true);
        }

        private static nint FindGetIdChild(nint parent, string title)
        {
            nint exact = 0;
            nint partial = 0;
            // 原版はMDI子に限らず、通常の子と孫も検索します。
            Native.EnumChildWindows(parent, (handle, _) =>
            {
                string caption = Text(handle);
                if (caption.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    exact = handle;
                    return false;
                }

                if (partial == 0
                    && caption.Contains(title, StringComparison.OrdinalIgnoreCase))
                {
                    partial = handle;
                }

                return true;
            }, 0);
            return exact != 0 ? exact : partial;
        }

        private Value InvokeStatus(CallArguments args)
        {
            nint window = Resolve(args.Int(0));
            var item = (WindowStatusItem)args.Int(1);
            if (item == WindowStatusItem.IsId)
            {
                return Value.Logical(window != 0);
            }

            if (window == 0)
            {
                return item switch
                {
                    WindowStatusItem.Iconic or WindowStatusItem.Maximized
                        or WindowStatusItem.Visible or WindowStatusItem.Active
                        or WindowStatusItem.Busy or WindowStatusItem.Win64
                        => Value.Logical(false),
                    WindowStatusItem.Title or WindowStatusItem.Class
                        or WindowStatusItem.Path => Value.String(""),
                    WindowStatusItem.Parent => N(-1),
                    _ => N(0)
                };
            }

            if (item is >= WindowStatusItem.X and <= WindowStatusItem.ClientHeight)
            {
                using var dpi = new PhysicalDpiScope();
                Native.Rect rect = Rect(window,
                    item >= WindowStatusItem.ClientX);
                var point = new Native.Point(0, 0);
                if (item is WindowStatusItem.ClientX or WindowStatusItem.ClientY)
                {
                    Check(Native.ClientToScreen(
                        window, ref point));
                }

                return N(item switch
                {
                    WindowStatusItem.X => rect.Left,
                    WindowStatusItem.Y => rect.Top,
                    WindowStatusItem.Width or WindowStatusItem.ClientWidth => rect.Right - rect.Left,
                    WindowStatusItem.Height or WindowStatusItem.ClientHeight => rect.Bottom - rect.Top,
                    WindowStatusItem.ClientX => point.X,
                    _ => point.Y
                });
            }

            switch (item)
            {
                case WindowStatusItem.Title:
                    return Value.String(Text(window));
                case WindowStatusItem.Iconic:
                    return Value.Logical(Native.IsIconic(window));
                case WindowStatusItem.Maximized:
                    return Value.Logical(Native.IsZoomed(window));
                case WindowStatusItem.Visible:
                    return Value.Logical(Native.IsWindowVisible(window));
                case WindowStatusItem.Active:
                    return Value.Logical(
                        Native.GetForegroundWindow() == window);
                case WindowStatusItem.Class:
                    return Value.String(Text(window,
                        true));
                case WindowStatusItem.Busy:
                    return Value.Logical(Native.SendMessageTimeoutW(
                        window, 0, 0, 0, Native.SmtoAbortIfHung,
                        Native.MessageTimeoutMilliseconds, out _) == 0);
                case WindowStatusItem.Parent:
                    return N(Register(
                        Native.GetParent(window)));
                case WindowStatusItem.Process:
                    Native.GetWindowThreadProcessId(
                        window, out uint processId);
                    return N(processId);
                case WindowStatusItem.Path:
                case WindowStatusItem.Win64:
                    Native.GetWindowThreadProcessId(
                        window, out uint targetProcess);
                    nint process = Native.OpenProcess(
                        0x1000, false, targetProcess);
                    if (process == 0)
                    {
                        throw WindowsOperationErrors.Record(
                            new Win32Exception(
                            Marshal.GetLastWin32Error()));
                    }

                    try
                    {
                        if (item == WindowStatusItem.Win64)
                        {
                            Check(
                                Native.IsWow64Process2(process, out ushort machine,
                                out ushort nativeMachine));
                            ushort effectiveMachine = machine
                                == 0 ? nativeMachine : machine;
                            return Value.Logical(
                                effectiveMachine is 0x8664 or 0xaa64 or 0xa641);
                        }

                        var path = new StringBuilder(
                            32768);
                        uint size = (uint)path.Capacity;
                        Check(
                            Native.QueryFullProcessImageNameW(process, 0, path, ref size));
                        return Value.String(
                            path.ToString());
                    }
                    finally
                    {
                        Native.CloseHandle(process);
                    }

                case WindowStatusItem.Monitor:
                    nint monitor = Native.MonitorFromWindow(
                        window, 2);
                    int index = 0;
                    int result = -1;
                    bool enumerated = Native.EnumDisplayMonitors(0, 0,
                        (nint current, nint dc, ref Native.Rect rect, nint data) =>
                    {
                        if (current == monitor)
                        {
                            result = index;
                        }

                        index++;
                        return true;
                    }, 0);
                    if (!enumerated || result < 0)
                    {
                        throw WindowsOperationErrors.Record(
                            new InvalidOperationException(
                                "ウィンドウのモニターを取得できませんでした。"));
                    }

                    return N(result);
                default:
                    throw WindowsOperationErrors.Record(
                        new NotSupportedException(
                        $"STATUSの項目{(int)item}は未対応です。"));
            }
        }

        private Value InvokeCtrlWin(CallArguments args)
        {
            var request =
                WindowControlArguments.Read(args.Values);
            nint window = Resolve(request.Id);
            if (window == 0)
            {
                return Value.Empty;
            }

            uint operation = request.Command;
            if (operation == 1)
            {
                // 前面化はOSが拒否できる要求であり、強制や全体設定変更は行いません。
                _ = windowActivation.RequestForeground(window);
            }
            else if (operation is 2 or 3)
            {
                Check(Native.PostMessageW(window,
                    operation == 2 ? 0x112u : 0x12u, operation == 2 ? 0xf060
                    : 0, 0));
            }
            else if (operation == 8)
            {
                // 最大化への復元履歴があっても、明示された通常状態へ戻します。
                Native.ShowWindow(window, 5);
                Native.ShowWindow(window, 1);
            }
            else if (operation is >= 4 and <= 7)
            {
                Native.ShowWindow(window,
                    operation switch
                    {
                        4 => 0,
                        5 => 5,
                        6 => 6,
                        _ => 3
                    });
            }
            else if (operation is >= 9 and <= 11)
            {
                SetWindowOrder(window, operation);
            }
            else
            {
                Check(Native.PostMessageW(window, operation,
                    (nint)(long)request.WParam, request.LParam));
            }

            return Value.Empty;
        }

        private static void SetWindowOrder(nint window, uint operation)
        {
            uint flags = Native.SwpNoSize | Native.SwpNoMove
                | (operation == 11 ? Native.SwpNoActivate : 0);
            Check(Native.SetWindowPos(window,
                operation == 10 ? -2 : -1, 0, 0, 0, 0, flags));
            if (operation == 10
                || (Native.GetWindowLongW(window, -20) & 8) != 0)
            {
                return;
            }

            // 表示状態にかかわらず、成功後も属性が付かなければZ順序を更新します。
            // 表示・アクティブ化・サイズ変更は行わず、追加要求は2回に限定します。
            uint recoveryFlags = flags | Native.SwpNoActivate;
            Check(Native.SetWindowPos(window, 1,
                0, 0, 0, 0, recoveryFlags));
            Check(Native.SetWindowPos(window, -1,
                0, 0, 0, 0, recoveryFlags));
        }

        private async ValueTask<Value> InvokeAcwAsync(
            CallArguments args, CancellationToken token)
        {
            await DelayArgumentAsync(args, 5, token).ConfigureAwait(false);
            int id = args.Int(0) == 0 ? lastWindow : args.Int(0);
            nint window = Resolve(id, false);
            if (window == 0)
            {
                return Value.Empty;
            }

            lastWindow = id;
            using var dpi = new PhysicalDpiScope();
            Native.Rect bounds = Rect(window);
            Native.Point position = AcwPosition(window, bounds);
            int width = args.Int(3);
            int height = args.Int(4);
            bool move = args.Has(1) && args.Int(1) != position.X
                || args.Has(2) && args.Int(2) != position.Y;
            bool resize = width != 0 && width != bounds.Right - bounds.Left
                || height != 0 && height != bounds.Bottom - bounds.Top;
            if (move || resize)
            {
                windowActivation.ShowNormal(window);
                token.ThrowIfCancellationRequested();
            }

            windowActivation.RequestForeground(window);
            token.ThrowIfCancellationRequested();
            bounds = Rect(window);
            position = AcwPosition(window, bounds);
            uint flags = Native.SwpNoZOrder | Native.SwpNoActivate;
            if (!move)
            {
                flags |= Native.SwpNoMove;
            }

            if (!resize)
            {
                flags |= Native.SwpNoSize;
            }

            Check(Native.SetWindowPos(window, 0,
                args.Int(1, position.X), args.Int(2, position.Y),
                width == 0 ? bounds.Right - bounds.Left : width,
                height == 0 ? bounds.Bottom - bounds.Top : height, flags));
            return Value.Empty;
        }

        private static Native.Point AcwPosition(nint window, Native.Rect bounds)
        {
            var position = new Native.Point(bounds.Left, bounds.Top);
            if ((Native.GetWindowLongW(window, Native.GwlStyle) & Native.WsChild) != 0)
            {
                Check(Native.ScreenToClient(Native.GetParent(window), ref position));
            }

            return position;
        }
    }
}
