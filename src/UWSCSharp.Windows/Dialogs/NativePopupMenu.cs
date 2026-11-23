using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDialogService
{
    private readonly SemaphoreSlim popupGate = new(1, 1);

    internal async Task<Value> ShowPopupAsync(object owner, PopupMenuRequest request,
        string title, nint parent, CancellationToken token)
    {
        await popupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await dispatcher.InvokeAsync(() =>
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(closing, this);
                }

                token.ThrowIfCancellationRequested();
                EnsureClass();
                var popup = new PopupWindow(this, owner, request, title, parent, token);
                return popup.Show();
            }, token).ConfigureAwait(false);
        }
        finally
        {
            popupGate.Release();
        }
    }

    private sealed class PopupWindow(NativeDialogService service, object owner,
        PopupMenuRequest request, string title, nint parent, CancellationToken token) : OwnedDialog
    {
        internal readonly TaskCompletionSource Finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override object Owner => owner;
        private Exception? failure;
        private CancellationToken? canceled;
        private bool tracking;

        internal Value Show()
        {
            var self = GCHandle.Alloc(this);
            nint menu = 0;
            try
            {
                Dictionary<uint, int> indices = [];
                menu = Build(request.Items, indices, token);
                token.ThrowIfCancellationRequested();
                Check(Native.CreateWindowExW(0x80, service.className, title,
                    0x80000000, 0, 0, 0, 0, parent, 0, service.instance,
                    GCHandle.ToIntPtr(self)) != 0);
                service.dispatcher.RegisterDialog(Handle, Fail);
                Native.Point position;
                if (request.X.HasValue && request.Y.HasValue)
                {
                    position = new Native.Point(request.X.Value, request.Y.Value);
                }
                else
                {
                    Check(Native.GetPhysicalCursorPos(out position));
                }

                using CancellationTokenRegistration registration = token.UnsafeRegister(_ =>
                {
                    Task close = service.dispatcher.InvokeAsync(() =>
                    {
                        Cancel(token);
                        return 0;
                    });
                    _ = close.ContinueWith(task => _ = task.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                }, null);
                token.ThrowIfCancellationRequested();
                Native.ShowWindow(Handle, 5);
                Native.SetForegroundWindow(Handle);
                tracking = true;
                uint command;
                try
                {
                    command = TrackPopupMenuEx(menu, 0x102, position.X, position.Y, Handle, 0);
                    if (command == 0 && Marshal.GetLastPInvokeError() != 0)
                    {
                        failure ??= NativeError();
                    }
                }
                finally
                {
                    tracking = false;
                }

                if (failure is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }

                token.ThrowIfCancellationRequested();
                if (canceled.HasValue)
                {
                    throw new OperationCanceledException(canceled.Value);
                }

                int index = command == 0 ? -1 : indices[command];
                return Value.Numeric(index) with
                {
                    NativeVariantType = 3,
                    NativeScalar = index
                };
            }
            finally
            {
                if (Handle != 0)
                {
                    Native.DestroyWindow(Handle);
                    service.windows.Remove(Handle);
                    service.dispatcher.RemoveDialog(Handle);
                }

                if (menu != 0)
                {
                    DestroyMenu(menu);
                }

                self.Free();
                Finished.TrySetResult();
            }
        }

        internal override nint Process(uint message, nint wParam, nint lParam)
        {
            if (message == 0x10)
            {
                if (tracking)
                {
                    EndMenu();
                }

                return 0;
            }

            return Native.DefWindowProcW(Handle, message, wParam, lParam);
        }

        internal override void Cancel(CancellationToken cancellation)
        {
            canceled = cancellation;
            if (tracking)
            {
                EndMenu();
            }
        }

        internal override void Fail(Exception error)
        {
            failure ??= error;
            if (tracking)
            {
                EndMenu();
            }
        }

        private static nint Build(IReadOnlyList<PopupMenuItem> items,
            Dictionary<uint, int> indices, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            nint menu = CreatePopupMenu();
            Check(menu != 0);
            try
            {
                for (int index = 0; index < items.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    PopupMenuItem item = items[index];
                    token.ThrowIfCancellationRequested();
                    nint child = 0;
                    try
                    {
                        if (item.Children.Count != 0)
                        {
                            child = Build(item.Children, indices, token);
                            Check(AppendMenuW(menu, 0x10, (nuint)child, item.Text));
                            child = 0;
                        }
                        else if (item.Separator)
                        {
                            Check(AppendMenuW(menu, 0x800, 0, null));
                        }
                        else
                        {
                            uint id = checked((uint)indices.Count + 1);
                            indices.Add(id, item.SelectionIndex);
                            if (!AppendMenuW(menu, 0, id, item.Text))
                            {
                                throw new System.ComponentModel.Win32Exception(
                                    Marshal.GetLastPInvokeError(),
                                    $"メニュー項目を追加できませんでした（添字: {item.Index}）。");
                            }
                        }
                    }
                    finally
                    {
                        if (child != 0)
                        {
                            DestroyMenu(child);
                        }
                    }
                }

                return menu;
            }
            catch
            {
                DestroyMenu(menu);
                throw;
            }
        }
    }
}
