using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有するSTAと非表示の通知窓で、ダイアログのUI操作を実行します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeDialogDispatcher : IAsyncDisposable
{
    private const uint WorkMessage = 0x8001;
    private const uint StopMessage = 0x8002;
    private const int UserData = -21;
    private static readonly WindowProcedure Procedure = Dispatch;
    private readonly object gate = new();
    private readonly ConcurrentQueue<IWork> pending = new();
    private readonly Dictionary<nint, Action<Exception>> dialogs = [];
    private readonly TaskCompletionSource ready = NewSignal();
    private readonly TaskCompletionSource ended = NewSignal();
    private readonly Thread thread;
    private readonly string className = "UWSCSharp.DialogQueue." + Guid.NewGuid().ToString("N");
    private nint window;
    private bool closing;
    private bool notificationPending;
    private Task? disposal;
    private Exception? fatal;

    internal NativeDialogDispatcher()
    {
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "UWSC# dialog UI"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal Task Ready => ready.Task;

    internal int ManagedThreadId => thread.ManagedThreadId;

    internal bool IsAlive => thread.IsAlive;

    internal void RegisterDialog(nint handle, Action<Exception> shutdown)
    {
        if (Thread.CurrentThread != thread)
        {
            throw new InvalidOperationException("ダイアログの登録は所有UI threadで行ってください。");
        }

        dialogs.Add(handle, shutdown);
    }

    internal void RemoveDialog(nint handle) => dialogs.Remove(handle);

    internal async Task<T> InvokeAsync<T>(Func<T> operation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        token.ThrowIfCancellationRequested();
        await ready.Task.WaitAsync(token).ConfigureAwait(false);
        var work = new Work<T>(operation, token);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            pending.Enqueue(work);
            if (!notificationPending)
            {
                notificationPending = true;
                if (!Native.PostMessageW(window, WorkMessage, 0, 0))
                {
                    notificationPending = false;
                    work.Fail(NativeFailure());
                }
            }
        }

        return await work.Completion.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Thread.CurrentThread == thread)
        {
            throw new InvalidOperationException("UI操作の実行中にdispatcher自身を解放できません。");
        }

        lock (gate)
        {
            closing = true;
            disposal ??= StopAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task StopAsync()
    {
        Exception? error = null;
        try
        {
            await ready.Task.ConfigureAwait(false);
            lock (gate)
            {
                if (window != 0 && !Native.PostMessageW(window, StopMessage, 0, 0))
                {
                    throw NativeFailure();
                }
            }
        }
        catch (Exception failure)
        {
            error = failure;
        }

        try
        {
            await ended.Task.ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            error = error is null || ReferenceEquals(error, failure)
                ? failure : new AggregateException(error, failure);
        }
        finally
        {
            thread.Join();
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private void Run()
    {
        nint instance = 0;
        bool registered = false;
        GCHandle self = default;
        try
        {
            using var dpi = new PhysicalDpiScope();
            instance = Native.GetModuleHandleW(null);
            var definition = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                Instance = instance,
                ClassName = className
            };
            Check(Native.RegisterClassExW(ref definition) != 0);
            registered = true;
            self = GCHandle.Alloc(this);
            nint created = Native.CreateWindowExW(0, className, "", 0,
                0, 0, 0, 0, -3, 0, instance, GCHandle.ToIntPtr(self));
            Check(created != 0);
            lock (gate)
            {
                window = created;
            }

            ready.TrySetResult();
            while (true)
            {
                int status = Native.GetMessageW(out Native.Message message, 0, 0, 0);
                Check(status >= 0);
                if (status == 0)
                {
                    lock (gate)
                    {
                        if (!closing && fatal is null)
                        {
                            fatal = new InvalidOperationException("ダイアログのUI処理が予期せず終了しました。");
                        }
                    }

                    break;
                }

                bool handled = false;
                foreach (nint dialog in dialogs.Keys.ToArray())
                {
                    if ((dialog == message.Window || Native.IsChild(dialog, message.Window))
                        && IsDialogMessageW(dialog, ref message))
                    {
                        handled = true;
                        break;
                    }
                }

                if (handled)
                {
                    continue;
                }

                _ = Native.TranslateMessage(ref message);
                _ = Native.DispatchMessageW(ref message);
            }
        }
        catch (Exception error)
        {
            AddFailure(error);
        }
        finally
        {
            foreach (Action<Exception> shutdown in dialogs.Values.ToArray())
            {
                try
                {
                    shutdown(fatal ?? new ObjectDisposedException(nameof(NativeDialogDispatcher)));
                }
                catch (Exception error)
                {
                    AddFailure(error);
                }
            }

            nint owned;
            lock (gate)
            {
                closing = true;
                owned = window;
                window = 0;
            }

            bool detached = owned == 0;
            if (owned != 0)
            {
                try
                {
                    SetData(owned, 0);
                    detached = true;
                }
                catch (Exception error)
                {
                    AddFailure(error);
                }

                if (Native.DestroyWindow(owned))
                {
                    detached = true;
                }
                else
                {
                    AddFailure(NativeFailure());
                }
            }

            if (self.IsAllocated && detached)
            {
                self.Free();
            }

            if (registered && !Native.UnregisterClassW(className, instance))
            {
                AddFailure(NativeFailure());
            }

            Exception stopped = fatal ?? new ObjectDisposedException(nameof(NativeDialogDispatcher));
            ready.TrySetException(stopped);
            while (pending.TryDequeue(out IWork? work))
            {
                work.Fail(stopped);
            }

            if (fatal is not null)
            {
                ended.TrySetException(fatal);
            }
            else
            {
                ended.TrySetResult();
            }
        }
    }

    private void Drain()
    {
        lock (gate)
        {
            // 到達した通知は消費済みです。モーダル中の再入にも次の通知を送ります。
            notificationPending = false;
        }

        // 操作の例外は要求ごとに返し、後続の要求やメッセージ処理を止めません。
        while (pending.TryDequeue(out IWork? work))
        {
            work.Run();
        }
    }

    private void AddFailure(Exception error) => fatal = fatal is null
        ? error : new AggregateException(fatal, error);

    private static nint Dispatch(nint window, uint message, nint wParam, nint lParam)
    {
        NativeDialogDispatcher? owner = null;
        try
        {
            if (message == 0x81)
            {
                nint data = Marshal.ReadIntPtr(lParam);
                owner = (NativeDialogDispatcher?)GCHandle.FromIntPtr(data).Target;
                SetData(window, data);
            }
            else
            {
                nint data = GetData(window);
                owner = data == 0 ? null : (NativeDialogDispatcher?)GCHandle.FromIntPtr(data).Target;
            }

            if (owner is not null && message == WorkMessage)
            {
                owner.Drain();
                return 0;
            }

            if (owner is not null && message == StopMessage)
            {
                Native.PostQuitMessage(0);
                return 0;
            }

            if (message == 0x82)
            {
                SetData(window, 0);
            }

            return Native.DefWindowProcW(window, message, wParam, lParam);
        }
        catch (Exception error)
        {
            // 管理例外をWndProcのネイティブ境界へ流しません。
            owner?.AddFailure(error);
            Native.PostQuitMessage(0);
            return 0;
        }
    }

    private static nint GetData(nint window) => IntPtr.Size == 8
        ? GetWindowLongPtrW(window, UserData) : Native.GetWindowLongW(window, UserData);

    private static void SetData(nint window, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = IntPtr.Size == 8 ? SetWindowLongPtrW(window, UserData, value)
            : SetWindowLongW(window, UserData, (int)value);
        if (previous == 0 && Marshal.GetLastWin32Error() != 0)
        {
            throw NativeFailure();
        }
    }

    private static Exception NativeFailure() =>
        WindowsOperationErrors.Record(new Win32Exception(Marshal.GetLastWin32Error()));

    private static void Check(bool success)
    {
        if (!success)
        {
            throw NativeFailure();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private interface IWork
    {
        void Run();

        void Fail(Exception error);
    }

    private sealed class Work<T>(Func<T> operation, CancellationToken token) : IWork
    {
        private readonly TaskCompletionSource<T> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<T> Completion => completion.Task;

        public void Run()
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                completion.TrySetCanceled(token);
                return;
            }

            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }

        public void Fail(Exception error) => completion.TrySetException(error);
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
}
