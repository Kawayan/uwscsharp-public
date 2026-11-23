using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDialogService
{
    private (int X, int Y)? lastSelectionPosition;

    internal async Task<Value> ShowSelectionAsync(object owner, SelectionBoxRequest request,
        DialogAppearance appearance, nint parent, CancellationToken token)
    {
        SelectionWindow window = await dispatcher.InvokeAsync(() =>
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
            }

            EnsureClass();
            var created = new SelectionWindow(this, owner, request, appearance, parent, token);
            created.Create();
            return created;
        }, token).ConfigureAwait(false);
        using CancellationTokenRegistration registration = token.UnsafeRegister(_ =>
        {
            Task close = dispatcher.InvokeAsync(() =>
            {
                window.Cancel(token);
                return 0;
            });
            _ = close.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    window.Completion.TrySetException(task.Exception!.InnerExceptions);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }, null);
        return await window.Completion.Task.ConfigureAwait(false);
    }

    private sealed partial class SelectionWindow(NativeDialogService service, object owner,
        SelectionBoxRequest request, DialogAppearance appearance, nint parent,
        CancellationToken token) : OwnedDialog
    {
        internal readonly TaskCompletionSource<Value> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override object Owner => owner;
        private readonly List<nint> rows = [];
        private readonly HashSet<int> selected = [];
        private GCHandle self;
        private nint font;
        private nint list;
        private nint progress;
        private int offset;
        private int pageSize;
        private int defaultId;
        private int thumb;
        private bool released;
        private bool registered;
        private Exception? failure;
        private CancellationToken? canceled;
        private Value result = SelectionBoxRequest.Closed;
        private SelectionBoxDeadline? deadline;
        private bool UsesVirtualList =>
            request.Kind is 8 or 16 && request.Items.Count >= 100_000;

        private bool UsesVirtualCombo => UsesVirtualList && request.Kind == 8;
        private nint comboButton;
        private bool comboOpen;
        private int comboIndex;
        private int comboExtraHeight;
        private Native.Rect closedComboBounds;
        private SubclassProcedure? comboKeys;
        private SubclassProcedure? rowPageKeys;

        [StructLayout(LayoutKind.Sequential)]
        private struct SelectionDrawItem
        {
            internal uint ControlType;
            internal uint ControlId;
            internal uint ItemId;
            internal uint Action;
            internal uint State;
            internal nint Window;
            internal nint Dc;
            internal Native.Rect Bounds;
            internal nuint Data;
        }

        internal void Create()
        {
            try
            {
                self = GCHandle.Alloc(this);
                (int X, int Y)? previous = request.X == -1 && request.Y == -1
                    ? service.lastSelectionPosition : null;
                int? x = previous?.X ?? (request.X == -1 && request.Y == -1 ? null : request.X);
                int? y = previous?.Y ?? (request.X == -1 && request.Y == -1 ? null : request.Y);
                if (x == -999999 && y == -999999)
                {
                    x = y = null;
                }

                nint monitor = x.HasValue && y.HasValue
                    ? Native.MonitorFromPoint(new Native.Point(x.Value, y.Value), 2)
                    : Native.MonitorFromWindow(parent, 2);
                var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
                Check(Native.GetMonitorInfoW(monitor, ref info));
                Native.Rect work = info.Work;
                bool paged = request.Kind is 1 or 2 or 4;
                uint style = 0x80c80000u | (paged ? 0x200000u : 0);
                nint handle = Native.CreateWindowExW(1, service.className, appearance.Title,
                    style, x ?? work.Left, y ?? work.Top, 100, 100,
                    parent, 0, service.instance, GCHandle.ToIntPtr(self));
                Check(handle != 0);
                uint dpi = GetDpiForWindow(handle);
                int Scale(int value) => ScaleAtDpi(value, dpi);
                int height = FontHeightAtDpi(appearance, dpi);
                int workHeight = work.Bottom - work.Top;
                int workWidth = work.Right - work.Left;
                if (height > workHeight / 6 || workWidth < Scale(240))
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException(
                        "指定フォントと画面サイズでは選択画面を配置できません。"));
                }

                font = CreateDialogFont(height, appearance.FontFamily);
                Check(font != 0);
                int padding = Scale(12);
                int gap = Scale(6);
                int rowHeight = Math.Max(Scale(28), height + padding);
                int bodyHeight = Scale(64);
                int maximumWidth = workWidth - padding * 4;
                int width = MeasureSelectionWidth(handle, dpi,
                    Math.Min(Scale(520), maximumWidth), maximumWidth);
                pageSize = Math.Max(1, Math.Min(12, (workHeight - bodyHeight - Scale(180)) / rowHeight));
                int shown = Math.Min(pageSize, Math.Max(1, request.Items.Count));
                int contentHeight = request.Kind == 8 ? rowHeight : shown * rowHeight;
                int contentTop = padding + bodyHeight + gap;
                int progressTop = contentTop + contentHeight + gap;
                int progressHeight = request.TimeoutSeconds > 0 ? Scale(14) : 0;
                int confirmTop = progressTop + (progressHeight > 0 ? progressHeight + gap : 0);
                int clientHeight = confirmTop + (request.Kind == 1 ? 0 : rowHeight) + padding;
                var bounds = new Native.Rect { Right = width, Bottom = clientHeight };
                Check(AdjustWindowRectEx(ref bounds, style, false, 1));
                int outerWidth = bounds.Right - bounds.Left;
                int outerHeight = bounds.Bottom - bounds.Top;
                Check(Native.SetWindowPos(handle, 0,
                    x ?? work.Left + (workWidth - outerWidth) / 2,
                    y ?? work.Top + (workHeight - outerHeight) / 2,
                    outerWidth, outerHeight, 0x14));

                nint message = CreateMessageControl(handle, service.instance,
                    padding, width - padding * 2, bodyHeight);
                InitializeMessageControl(message, font);
                Check(SetWindowTextW(message, request.Message.Replace("\0", "␀")));
                if (request.Kind == 4 && request.Items.Count > 0)
                {
                    selected.Add(0);
                }

                if (paged)
                {
                    if (request.Items.Count > shown)
                    {
                        rowPageKeys = ProcessRowPageKey;
                    }

                    for (int row = 0; row < shown && row < request.Items.Count; row++)
                    {
                        uint buttonStyle = request.Kind switch
                        {
                            2 => 3u,
                            4 => 9u,
                            _ => 0u
                        };
                        nint button = Native.CreateWindowExW(0,
                            ControlClassNames.Uppercase.Button, "",
                            0x50010000u | buttonStyle,
                            padding, contentTop + row * rowHeight,
                            width - padding * 2, rowHeight - gap, handle, 0x5000 + row,
                            service.instance, 0);
                        Check(button != 0);
                        rows.Add(button);
                        Native.SendMessageW(button, 0x30, font, 1);
                        if (rowPageKeys is not null)
                        {
                            Check(SetWindowSubclass(button, rowPageKeys,
                                1, 0));
                        }
                    }

                    UpdatePage();
                }
                else
                {
                    list = Native.CreateWindowExW(0x200,
                        request.Kind == 8 && !UsesVirtualCombo
                            ? ControlClassNames.Uppercase.ComboBox
                            : ControlClassNames.Uppercase.ListBox, "",
                        UsesVirtualCombo ? 0x40212d11u
                            : request.Kind == 8 ? 0x50210243u
                            : UsesVirtualList ? 0x50212d19u
                            : 0x50210941u,
                        padding, UsesVirtualCombo ? contentTop + rowHeight
                            : contentTop, width - padding * 2,
                        UsesVirtualCombo ? Scale(200)
                            : request.Kind == 8 ? rowHeight + Scale(200)
                            : contentHeight,
                        handle, 201, service.instance, 0);
                    Check(list != 0);
                    Native.SendMessageW(list, 0x30, font, 1);
                    if (UsesVirtualList)
                    {
                        token.ThrowIfCancellationRequested();
                        Check(Native.SendMessageW(list, 0x1a0, 0, rowHeight)
                            >= 0);
                        Check(Native.SendMessageW(list, 0x1a7,
                            request.Items.Count, 0) >= 0);
                        if (UsesVirtualCombo)
                        {
                            comboExtraHeight = Scale(200);
                            comboButton = Native.CreateWindowExW(0,
                                ControlClassNames.Uppercase.Button, "",
                                0x50010000, padding, contentTop,
                                width - padding * 2, rowHeight,
                                handle, 203, service.instance, 0);
                            Check(comboButton != 0);
                            Native.SendMessageW(comboButton, 0x30, font, 1);
                            UpdateComboLabel();
                            comboKeys = ProcessComboKey;
                            Check(SetWindowSubclass(comboButton, comboKeys,
                                1, 0));
                            Check(SetWindowSubclass(list, comboKeys, 1, 0));
                        }
                    }
                    else
                    {
                        // 項目をまとめて登録し、登録中の再描画を抑える。
                        Native.SendMessageW(list, 0x0b, 0, 0);
                        for (int index = 0; index < request.Items.Count; index++)
                        {
                            if (index % 32 == 0)
                            {
                                token.ThrowIfCancellationRequested();
                            }

                            nint text = Marshal.StringToHGlobalUni(request.Items[index].Replace("\0", "␀"));
                            try
                            {
                                nint added = Native.SendMessageW(list, request.Kind == 8 ? 0x143u : 0x180u, 0, text);
                                if ((long)added < 0)
                                {
                                    throw WindowsOperationErrors.Record(new InvalidOperationException(
                                        "選択項目を追加できませんでした。"));
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(text);
                            }
                        }

                        if (request.Kind == 8 && request.Items.Count > 0)
                        {
                            Native.SendMessageW(list, 0x14e, 0, 0);
                        }

                        Native.SendMessageW(list, 0x0b, 1, 0);
                    }
                }

                if (request.Kind != 1)
                {
                    nint confirm = Native.CreateWindowExW(0,
                        ControlClassNames.Uppercase.Button, "OK", 0x50010001,
                        padding, confirmTop, Math.Min(width - padding * 2, Scale(120)), rowHeight,
                        handle, 4097, service.instance, 0);
                    Check(confirm != 0);
                    Native.SendMessageW(confirm, 0x30, font, 1);
                }

                defaultId = request.Kind == 1 ? rows.Count > 0 ? 0x5000 : 0 : 4097;
                if (progressHeight > 0)
                {
                    var controls = new CommonControls { Size = 8, Classes = 0x20 };
                    Check(InitCommonControlsEx(ref controls));
                    progress = Native.CreateWindowExW(0, ControlClassNames.ProgressBar, "", 0x50000001,
                        padding, progressTop, width - padding * 2, progressHeight,
                        handle, 202, service.instance, 0);
                    Check(progress != 0);
                    Native.SendMessageW(progress, 0x406, 0, 1000);
                    Native.SendMessageW(progress, 0x402, 1000, 0);
                }

                service.dispatcher.RegisterDialog(handle, Fail);
                registered = true;
                token.ThrowIfCancellationRequested();
                deadline = new SelectionBoxDeadline(TimeProvider.System, request.TimeoutSeconds);
                if (deadline.Enabled)
                {
                    Check(Native.SetTimer(handle, 1, 50, 0) != 0);
                }

                Native.ShowWindow(handle, 5);
                Native.SetForegroundWindow(handle);
                nint focus = comboButton != 0 ? comboButton
                    : list != 0 ? list : rows.Count > 0 ? rows[0] : handle;
                Native.SetFocus(focus);
            }
            catch (Exception error)
            {
                failure = error;
                if (Handle != 0)
                {
                    Native.DestroyWindow(Handle);
                }
                else
                {
                    Release();
                }

                _ = Completion.Task.Exception;
                throw;
            }
        }

        internal override nint Process(uint message, nint wParam, nint lParam)
        {
            if (message == 2)
            {
                comboOpen = false;
            }

            if (message == 0x2b && wParam == 201 && UsesVirtualList)
            {
                DrawListItem(Marshal.PtrToStructure<SelectionDrawItem>(
                    lParam));
                return 1;
            }

            if (UsesVirtualList && lParam == list)
            {
                if (message == 0x2e)
                {
                    return -1;
                }

                if (message == 0x2f)
                {
                    return FindListItem((char)(long)wParam);
                }
            }

            if (message == 0x10)
            {
                Close(SelectionBoxRequest.Closed);
                return 0;
            }

            if (message == 0x113 && wParam == 1 && deadline is not null)
            {
                if (deadline.Expired)
                {
                    Close(request.TimedOut);
                }
                else
                {
                    Native.SendMessageW(progress, 0x402, (int)(deadline.RemainingFraction * 1000), 0);
                }

                return 0;
            }

            if (message == 0x400)
            {
                return defaultId == 0 ? 0 : (0x534b << 16) | defaultId;
            }

            if (message == 0x401)
            {
                if ((request.Kind != 1 && (int)wParam == 4097)
                    || (request.Kind == 1 && (int)wParam >= 0x5000 && (int)wParam < 0x5000 + rows.Count))
                {
                    defaultId = (int)wParam;
                }

                return 0;
            }

            if (message == 0x111)
            {
                int id = (int)((long)wParam & 0xffff);
                int notification = (int)((long)wParam >> 16);
                if (UsesVirtualCombo && id == 203 && notification == 0)
                {
                    ToggleCombo();
                }
                else if (comboOpen && id is 1 or 2 or 4097)
                {
                    HideCombo(commit: id != 2);
                }
                else if (id == 2)
                {
                    Close(SelectionBoxRequest.Closed);
                }
                else if (id is 1 or 4097)
                {
                    if (request.Kind == 1)
                    {
                        if (rows.Count > 0)
                        {
                            Close(request.Selected([offset + Math.Clamp(defaultId - 0x5000, 0, rows.Count - 1)]));
                        }
                    }
                    else
                    {
                        Close(request.Selected(ReadSelection()));
                    }
                }
                else if (id >= 0x5000 && id < 0x5000 + rows.Count && ((long)wParam >> 16) == 0)
                {
                    int row = id - 0x5000;
                    int index = offset + row;
                    if (index < request.Items.Count)
                    {
                        if (request.Kind == 1)
                        {
                            Close(request.Selected([index]));
                        }
                        else if (request.Kind == 2)
                        {
                            if (Native.SendMessageW(rows[row], 0xf0, 0, 0) != 0)
                            {
                                selected.Add(index);
                            }
                            else
                            {
                                selected.Remove(index);
                            }
                        }
                        else
                        {
                            selected.Clear();
                            selected.Add(index);
                            UpdatePage();
                        }
                    }
                }

                return 0;
            }

            if (rows.Count > 0 && (message == 0x115 || message == 0x20a))
            {
                int next = offset;
                if (message == 0x20a)
                {
                    next -= (short)(((long)wParam >> 16) & 0xffff) / 120 * 3;
                }
                else
                {
                    int command = (int)((long)wParam & 0xffff);
                    if (command == 5)
                    {
                        var scroll = new ScrollInfo { Size = 28, Mask = 16 };
                        Check(GetScrollInfo(Handle, 1, ref scroll));
                        thumb = scroll.Track;
                    }

                    next = command switch
                    {
                        0 => offset - 1,
                        1 => offset + 1,
                        2 => offset - pageSize,
                        3 => offset + pageSize,
                        4 or 5 => thumb,
                        6 => 0,
                        7 => request.Items.Count,
                        _ => offset
                    };
                }

                offset = Math.Clamp(next, 0, Math.Max(0, request.Items.Count - rows.Count));
                UpdatePage();
                return 0;
            }

            if (message == 0x82)
            {
                Release();
            }

            return Native.DefWindowProcW(Handle, message, wParam, lParam);
        }

        private nint ProcessRowPageKey(nint window, uint message, nint wParam,
            nint lParam, nuint subclass, nuint reference)
        {
            const int pageUp = 0x21;
            const int pageDown = 0x22;
            if (wParam is pageUp or pageDown)
            {
                if (message == 0x87)
                {
                    const int wantMessage = 0x0004;
                    return DefSubclassProc(window, message, wParam, lParam)
                        | wantMessage;
                }

                if (message == 0x100)
                {
                    // スクロールバーと同じ処理で選択状態を保ったまま表示を送る。
                    const uint verticalScroll = 0x115;
                    Native.SendMessageW(Handle, verticalScroll,
                        wParam == pageUp ? 2 : 3, 0);
                    return 0;
                }
            }

            return DefSubclassProc(window, message, wParam, lParam);
        }

        private nint ProcessComboKey(nint window, uint message, nint wParam,
            nint lParam, nuint subclass, nuint reference)
        {
            if (message == 0x100 && wParam == 0x73)
            {
                ToggleCombo();
                return 0;
            }

            nint result = DefSubclassProc(window, message, wParam, lParam);
            if (comboOpen && window == list)
            {
                if (message == 0x202 && IsComboItemAt(lParam))
                {
                    HideCombo(commit: true);
                }
                else if (message == 0x8)
                {
                    HideCombo(commit: false, restoreFocus: false);
                }
            }

            return result;
        }

        private bool IsComboItemAt(nint point)
        {
            int hit = (int)Native.SendMessageW(list, 0x1a9, 0, point);
            int current = (int)Native.SendMessageW(list, 0x188, 0, 0);
            // ヒット位置の添字は下位16ビットだけが返る。
            return current >= 0 && (hit >> 16) == 0
                && (ushort)hit == (ushort)current;
        }

        private void ToggleCombo()
        {
            if (comboOpen)
            {
                HideCombo(commit: true);
                return;
            }

            comboOpen = true;
            Check(Native.GetWindowRect(Handle, out closedComboBounds));
            nint monitor = Native.MonitorFromWindow(Handle, 2);
            var info = new Native.MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
            };
            Check(Native.GetMonitorInfoW(monitor, ref info));
            int closedHeight = closedComboBounds.Bottom - closedComboBounds.Top;
            int workHeight = info.Work.Bottom - info.Work.Top;
            int extraHeight = Math.Min(comboExtraHeight,
                Math.Max(0, workHeight - closedHeight));
            int height = closedHeight + extraHeight;
            int top = Math.Clamp(closedComboBounds.Top, info.Work.Top,
                Math.Max(info.Work.Top, info.Work.Bottom - height));
            ResizeComboWindow(top, height);
            Check(Native.GetWindowRect(list, out Native.Rect bounds));
            Check(Native.SetWindowPos(list, 0, 0, 0,
                bounds.Right - bounds.Left, extraHeight, 0x16));
            Native.SendMessageW(list, 0x186, comboIndex, 0);
            Native.SetWindowPos(list, 0, 0, 0, 0, 0, 0x43);
            Native.SetFocus(list);
        }

        private void HideCombo(bool commit, bool restoreFocus = true)
        {
            comboOpen = false;
            if (commit)
            {
                comboIndex = (int)Native.SendMessageW(list, 0x188, 0, 0);
                UpdateComboLabel();
            }

            Native.ShowWindow(list, 0);
            ResizeComboWindow(closedComboBounds.Top,
                closedComboBounds.Bottom - closedComboBounds.Top);
            if (restoreFocus)
            {
                Native.SetFocus(comboButton);
            }
        }

        private void ResizeComboWindow(int top, int height)
        {
            Check(Native.SetWindowPos(Handle, 0, closedComboBounds.Left, top,
                closedComboBounds.Right - closedComboBounds.Left,
                height, 0x14));
        }

        private void UpdateComboLabel()
        {
            string text = comboIndex < 0 ? ""
                : request.Items[comboIndex].Replace("\0", "␀");
            Check(SetWindowTextW(comboButton, text.Replace("&", "&&") + " ▼"));
        }

        private int FindListItem(char character)
        {
            if (char.IsControl(character) || character == ' ')
            {
                return -1;
            }

            int count = request.Items.Count;
            char upper = char.ToUpperInvariant(character);
            int caret = (int)Native.SendMessageW(list, 0x19f, 0, 0);
            int start = (caret + 1) % count;
            for (int step = 0; step < count; step++)
            {
                int index = (start + step) % count;
                string item = request.Items[index];
                if (item.Length != 0 && char.ToUpperInvariant(item[0])
                    == upper)
                {
                    return index;
                }
            }

            return -1;
        }

        private void DrawListItem(SelectionDrawItem item)
        {
            bool highlighted = (item.State & 1) != 0;
            FillRect(item.Dc, item.Bounds,
                GetSysColorBrush(highlighted ? 13 : 5));
            if (item.ItemId < (uint)request.Items.Count)
            {
                string text = request.Items[(int)item.ItemId]
                    .Replace("\0", "␀");
                int saved = BalloonSaveDC(item.Dc);
                if (saved != 0)
                {
                    try
                    {
                        ImageNative.SelectObject(item.Dc, font);
                        BalloonSetBkMode(item.Dc, 1);
                        BalloonSetTextColor(item.Dc,
                            GetSysColor(highlighted ? 14 : 8));
                        Native.Rect bounds = item.Bounds;
                        bounds.Left += 2;
                        DrawTextW(item.Dc, text, text.Length, ref bounds,
                            0x824);
                    }
                    finally
                    {
                        BalloonRestoreDC(item.Dc, saved);
                    }
                }
            }

            if ((item.State & 0x210) == 0x10)
            {
                DrawFocusRect(item.Dc, item.Bounds);
            }
        }

        private int MeasureSelectionWidth(nint handle, uint dpi,
            int width, int maximumWidth)
        {
            int textPadding = ScaleAtDpi(72, dpi);
            nint dc = ImageNative.GetDC(handle);
            Check(dc != 0);
            nint saved = ImageNative.SelectObject(dc, font);
            try
            {
                for (int index = 0; index < request.Items.Count
                    && width < maximumWidth; index++)
                {
                    token.ThrowIfCancellationRequested();
                    string text = request.Items[index].Replace("\0", "␀");
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    Check(GetTextExtentPoint32W(dc, text, text.Length,
                        out Size measured));
                    width = (int)Math.Clamp((long)measured.Width + textPadding,
                        width, maximumWidth);
                }

                return width;
            }
            finally
            {
                ImageNative.SelectObject(dc, saved);
                ImageNative.ReleaseDC(handle, dc);
            }
        }

        private void UpdatePage()
        {
            for (int row = 0; row < rows.Count; row++)
            {
                int index = offset + row;
                Check(SetWindowTextW(rows[row], request.Items[index].Replace("\0", "␀")));
                Native.SendMessageW(rows[row], 0xf1, selected.Contains(index) ? 1 : 0, 0);
            }

            var scroll = new ScrollInfo
            {
                Size = 28,
                Mask = 7,
                Maximum = Math.Max(0, request.Items.Count - 1),
                Page = (uint)rows.Count,
                Position = offset
            };
            SetScrollInfo(Handle, 1, ref scroll, true);
        }

        private IEnumerable<int> ReadSelection()
        {
            if (UsesVirtualCombo)
            {
                return comboIndex < 0 ? [] : [comboIndex];
            }

            if (request.Kind == 8)
            {
                int index = (int)Native.SendMessageW(list, 0x147, 0, 0);
                return index < 0 ? [] : [index];
            }

            if (request.Kind != 16)
            {
                return selected;
            }

            int count = (int)Native.SendMessageW(list, 0x190, 0, 0);
            if (count < 0)
            {
                throw WindowsOperationErrors.Record(new InvalidOperationException("選択状態を取得できません。"));
            }

            nint buffer = Marshal.AllocHGlobal(checked(Math.Max(1, count) * 4));
            try
            {
                int actual = (int)Native.SendMessageW(list, 0x191, count, buffer);
                if (actual < 0 || actual > count)
                {
                    throw WindowsOperationErrors.Record(new InvalidOperationException("選択項目を取得できません。"));
                }

                int[] result = new int[actual];
                Marshal.Copy(buffer, result, 0, actual);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal override void Cancel(CancellationToken token)
        {
            canceled = token;
            Close(SelectionBoxRequest.Closed);
        }

        internal override void Fail(Exception error)
        {
            failure = error;
            Close(SelectionBoxRequest.Closed);
        }

        private void Close(Value value)
        {
            if (released)
            {
                return;
            }

            result = value;
            bool wasOpen = comboOpen;
            comboOpen = false;
            if (wasOpen)
            {
                service.lastSelectionPosition =
                    (closedComboBounds.Left, closedComboBounds.Top);
            }
            else if (Native.GetWindowRect(Handle, out Native.Rect rect))
            {
                service.lastSelectionPosition = (rect.Left, rect.Top);
            }

            if (!Native.DestroyWindow(Handle))
            {
                failure ??= NativeError();
                Completion.TrySetException(failure);
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
            if (registered)
            {
                service.dispatcher.RemoveDialog(Handle);
            }

            if (font != 0)
            {
                if (!ImageNative.DeleteObject(font))
                {
                    failure ??= NativeError();
                }

                font = 0;
            }

            if (self.IsAllocated)
            {
                self.Free();
            }

            if (failure is not null)
            {
                Completion.TrySetException(failure);
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommonControls
    {
        internal uint Size;
        internal uint Classes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo
    {
        internal uint Size;
        internal uint Mask;
        internal int Minimum;
        internal int Maximum;
        internal uint Page;
        internal int Position;
        internal int Track;
    }
}
