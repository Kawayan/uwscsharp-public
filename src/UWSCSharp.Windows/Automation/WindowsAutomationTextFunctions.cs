using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private const int GetStrTitleLimit = 300;

        private async ValueTask<Value> InvokeGetStrAsync(
            CallArguments args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (args.Int(0) == 0)
            {
                return Value.String(clipboard.ReadText(token));
            }

            nint root = Resolve(args.Int(0), false);
            if (root == 0)
            {
                return Value.Empty;
            }

            int occurrence = args.Int(1);
            int kind = args.Int(2);
            bool move = args.Int(3) != 0;
            if (kind >= 3)
            {
                Native.Rect? bounds = null;
                string? text = await AccessibleItems.ReadStringAsync(root,
                    kind, occurrence, token,
                    move ? rectangle => bounds = rectangle : null).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (bounds is Native.Rect location)
                {
                    MoveToTextTarget(location, token);
                }

                return text is null ? Value.Empty : Value.String(text);
            }

            if (kind == 1 && occurrence == 0)
            {
                string caption = await ReadCaptionAsync(root, token).ConfigureAwait(false);
                return Value.String(caption[..Math.Min(caption.Length, GetStrTitleLimit)]);
            }

            if (kind == 2)
            {
                long part = Math.Max(1, Math.Abs((long)occurrence));
                List<nint> statuses = Controls(root,
                    [ControlClassNames.StatusBar], occurrence < 0, token);
                foreach (nint status in statuses)
                {
                    (int Count, List<string> Parts) read =
                        await NativeStatusControl.ReadAsync(status, token, part).ConfigureAwait(false);
                    if (part <= read.Count)
                    {
                        if (move)
                        {
                            Native.Rect bounds = await NativeStatusControl.ReadPartBoundsAsync(
                                status, checked((int)part), token).ConfigureAwait(false);
                            MoveToTextTarget(bounds, token);
                        }

                        return Value.String(read.Parts[0]);
                    }

                    part -= read.Count;
                }

                if (statuses.Count != 0)
                {
                    return Value.Empty;
                }
            }
            else
            {
                nint target = FindTextControl(root, occurrence, kind, token);
                if (target != 0)
                {
                    string text = ReadControlText(target);
                    token.ThrowIfCancellationRequested();
                    if (move)
                    {
                        Native.Rect bounds;
                        using (var dpi = new PhysicalDpiScope())
                        {
                            bounds = Rect(target);
                        }

                        MoveToTextTarget(bounds, token);
                    }

                    return Value.String(text);
                }
            }

            TextControlValue result = await UiaTextInput.ReadAsync(root,
                occurrence, kind, move, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Bounds is Native.Rect targetBounds)
            {
                MoveToTextTarget(targetBounds, token);
            }

            return result.Found ? Value.String(result.Text) : Value.Empty;
        }

        private static void MoveToTextTarget(Native.Rect bounds,
            CancellationToken token)
        {
            Native.Point point = TextTargetGeometry.Inside(bounds);
            token.ThrowIfCancellationRequested();
            using var dpi = new PhysicalDpiScope();
            Check(Native.SetPhysicalCursorPos(point.X, point.Y));
        }

        private async ValueTask<Value> InvokeSendStrAsync(
            CallArguments args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string text = args.Str(1);
            if (args.Int(0) == 0)
            {
                clipboard.WriteText(text, token);
                return Value.Empty;
            }

            nint root = Resolve(args.Int(0));
            if (root == 0)
            {
                return Value.Empty;
            }

            int occurrence = args.Int(2);
            int mode = args.Int(3);
            int accessibility = args.Int(4);
            if (accessibility != 0)
            {
                if (accessibility == 1)
                {
                    nint top = Native.GetAncestor(root, 2);
                    _ = windowActivation.RequestForeground(top == 0 ? root : top);
                }

                _ = await AccessibleItems.WriteStringAsync(root, text,
                    occurrence, mode, accessibility == 5, token).ConfigureAwait(false);
                return Value.Empty;
            }

            List<nint> edits = Controls(root, [], occurrence < 0, token,
                accepts: child => Text(child, true).Contains(
                    ControlClassNames.Uppercase.Edit, StringComparison.OrdinalIgnoreCase));
            nint target = 0;
            if (occurrence == 0)
            {
                uint thread = Native.GetWindowThreadProcessId(root, out _);
                var info = new Native.GuiThreadInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>()
                };
                if (Native.GetGUIThreadInfo(thread, ref info)
                    && (info.Focus == root || Native.IsChild(root, info.Focus))
                    && Text(info.Focus, true).Contains(
                        ControlClassNames.Uppercase.Edit, StringComparison.OrdinalIgnoreCase))
                {
                    target = info.Focus;
                }
            }
            else
            {
                long index = Math.Abs((long)occurrence) - 1;
                if (index < edits.Count)
                {
                    target = edits[(int)index];
                }
            }

            if (target == 0)
            {
                if (mode != 2)
                {
                    _ = await UiaTextInput.WriteAsync(root, text,
                        occurrence, mode, token).ConfigureAwait(false);
                }

                return Value.Empty;
            }

            token.ThrowIfCancellationRequested();
            if (mode == 0 && occurrence == 0 && text.Length == 0)
            {
                return Value.Empty;
            }

            if (mode == 2)
            {
                foreach (char character in text)
                {
                    token.ThrowIfCancellationRequested();
                    Check(Native.PostMessageW(target, 0x102, character, 0));
                }
            }
            else
            {
                bool insertAtSelection = mode == 0 && occurrence == 0;
                if (mode == 0 && !insertAtSelection)
                {
                    text = ReadControlText(target) + text;
                }

                if (text.Length > NativeListControl.MaximumCharacters)
                {
                    throw WindowsOperationErrors.Record(
                        new InvalidOperationException("送信文字数が上限を超えました。"));
                }

                token.ThrowIfCancellationRequested();
                if (Native.SendText(target, insertAtSelection ? 0xc2u : 0xcu,
                    insertAtSelection ? 1 : 0, text, Native.SmtoAbortIfHung,
                    Native.MessageTimeoutMilliseconds, out _) == 0)
                {
                    throw WindowsOperationErrors.Record(
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if (!insertAtSelection)
                {
                    token.ThrowIfCancellationRequested();
                    _ = ControlMessage(target, 0xb9, token, 1);
                }
            }

            return Value.Empty;
        }

        private static nint FindTextControl(nint root, int occurrence,
            int kind, CancellationToken token)
        {
            if (root == 0)
            {
                return 0;
            }

            string className = kind == 0
                ? ControlClassNames.Edit : ControlClassNames.Static;
            List<nint> controls = Controls(root, [className],
                occurrence < 0, token, accepts: child =>
                    NativeTextControlNames.Matches(Text(child, true), className));
            if (occurrence == 0)
            {
                uint thread = Native.GetWindowThreadProcessId(root, out _);
                var info = new Native.GuiThreadInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>()
                };
                return Native.GetGUIThreadInfo(thread, ref info)
                    && (info.Focus == root || Native.IsChild(root, info.Focus))
                    && NativeTextControlNames.Matches(Text(info.Focus, true), className)
                    ? info.Focus : 0;
            }

            long index = Math.Abs((long)occurrence) - 1;
            return index < controls.Count ? controls[(int)index] : 0;
        }

        private static string ReadControlText(nint window)
        {
            bool isStatic = NativeTextControlNames.Matches(
                Text(window, true), ControlClassNames.Static);
            if (isStatic && IsResourceStatic(window))
            {
                return "";
            }

            if (Native.SendMessageTimeoutW(window, Native.WmGetTextLength, 0,
                0, Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out nuint length) == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(
                    Marshal.GetLastWin32Error()));
            }

            if (length > 16_777_216)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException(
                    "コントロールの文字列が長すぎます。"));
            }

            var buffer = new StringBuilder(Math.Max(4, (int)length + 1));
            if (Native.ReadText(window, Native.WmGetText,
                buffer.Capacity, buffer, Native.SmtoAbortIfHung, Native.MessageTimeoutMilliseconds, out _) == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(
                    Marshal.GetLastWin32Error()));
            }

            return isStatic && IsResourceStatic(window)
                ? "" : buffer.ToString();
        }

        private static bool IsResourceStatic(nint window) =>
            (Native.GetWindowLongW(window, Native.GwlStyle) & 0x1f) is 3 or 14 or 15;

        private static Task<string> ReadCaptionAsync(nint target, CancellationToken token)
        {
            if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
            {
                return Task.FromResult(ReadCaption(target, token));
            }

            return NativeControlWorker.RunAsync(active => ReadCaption(target, active), token);
        }

        private static string ReadCaption(nint target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool isStatic = NativeTextControlNames.Matches(
                Text(target, true), ControlClassNames.Static);
            if (isStatic && IsResourceStatic(target))
            {
                return "";
            }

            int length = Native.GetWindowTextLengthW(target);
            if (length > NativeListControl.MaximumCharacters)
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("captionの文字列長が取得上限を超えました。"));
            }

            var buffer = new StringBuilder(Math.Max(256, length + 1));
            Native.GetWindowTextW(target, buffer, buffer.Capacity);
            token.ThrowIfCancellationRequested();
            if (!Native.IsWindow(target))
            {
                throw WindowsOperationErrors.Record(
                    new InvalidOperationException("caption取得中にwindowが破棄されました。"));
            }

            return isStatic && IsResourceStatic(target)
                ? "" : buffer.ToString();
        }
    }
}
