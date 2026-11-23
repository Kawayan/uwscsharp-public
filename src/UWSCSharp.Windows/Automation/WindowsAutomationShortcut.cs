using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session
    {
        private static readonly TimeSpan ShortcutFocusTimeout = TimeSpan.FromSeconds(1);
        private const int ShortcutKeyDelayMilliseconds = 20;
        private const int ShortcutCompletionDelayMilliseconds = 20;
        private const int ShortcutFocusPollMilliseconds = 10;

        bool IShortcutKeyBackend.IsHeld(int rawKey) => keys.Any(held =>
                ShortcutKeySequence.PhysicalKey(held)
                    == ShortcutKeySequence.PhysicalKey(rawKey));

        ValueTask IShortcutKeyBackend.SendAsync(int rawKey, int mode,
            CancellationToken cancellationToken) => SendShortcutKeyAsync(
                rawKey, mode, cancellationToken);

        Task IShortcutKeyBackend.DelayAsync(CancellationToken cancellationToken) =>
            DelayMillisecondsAsync(ShortcutKeyDelayMilliseconds, cancellationToken);

        private async ValueTask SendShortcutKeyAsync(int rawKey, int mode,
            CancellationToken token)
        {
            // SCKEYはMOUSEORGの直接送信先を使わず、物理入力へ送ります。
            token.ThrowIfCancellationRequested();
            if (mode != 2)
            {
                await KeyAsync(rawKey, false, token).ConfigureAwait(false);
            }

            if (mode != 1)
            {
                await KeyAsync(rawKey, true).ConfigureAwait(false);
            }
        }

        private async ValueTask<Value> InvokeScKeyAsync(
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            var request = ShortcutKeyArguments.Read(arguments);
            nint target = Resolve(request.WindowId);
            if (target == 0)
            {
                return Value.Empty;
            }

            bool iconic = Native.IsIconic(target);
            bool visible = Native.IsWindowVisible(target);
            nint foreground = iconic || !visible
                ? lookup.ForegroundWindow() : 0;
            Exception? primary = null;
            try
            {
                if (iconic || !visible)
                {
                    Native.ShowWindow(target, iconic ? 9 : 5);
                }

                token.ThrowIfCancellationRequested();
                IShortcutKeyBackend backend = request.WindowId == 0
                    ? this : new ShortcutTargetBackend(this, target);
                await ShortcutKeySequence.RunAsync(request.Keys, backend, token)
                    .ConfigureAwait(false);
                await DelayMillisecondsAsync(ShortcutCompletionDelayMilliseconds, token).ConfigureAwait(false);
                return Value.Empty;
            }
            catch (Exception error)
            {
                primary = error;
                throw;
            }
            finally
            {
                try
                {
                    if (Native.IsWindow(target))
                    {
                        if (iconic)
                        {
                            Native.ShowWindow(target, 6);
                        }

                        if (!visible)
                        {
                            Native.ShowWindow(target, 0);
                        }
                    }

                    if (foreground != 0 && Native.IsWindow(foreground))
                    {
                        windowActivation.RequestForeground(foreground);
                    }
                }
                catch (Exception cleanup) when (primary is not null)
                {
                    IEnumerable<Exception> previous = primary.Data[
                        ShortcutKeySequence.CleanupErrorsKey] is AggregateException errors
                            ? errors.InnerExceptions : [];
                    primary.Data[ShortcutKeySequence.CleanupErrorsKey] =
                        new AggregateException(previous.Append(cleanup));
                }
            }
        }

        private sealed class ShortcutTargetBackend(Session session,
            nint target) : IShortcutKeyBackend
        {
            private bool started;

            public bool IsHeld(int rawKey) =>
                ((IShortcutKeyBackend)session).IsHeld(rawKey);

            public Task DelayAsync(CancellationToken cancellationToken) =>
                ((IShortcutKeyBackend)session).DelayAsync(cancellationToken);

            public async ValueTask SendAsync(int rawKey, int mode,
                CancellationToken cancellationToken)
            {
                // UPは対象の消滅や前面化失敗でも必ず試みます。
                if (mode != 2)
                {
                    if (!started)
                    {
                        await session.PrepareShortcutTargetAsync(target,
                            cancellationToken).ConfigureAwait(false);
                    }

                    session.VerifyShortcutTarget(target);
                    started = true;
                }

                await session.SendShortcutKeyAsync(rawKey, mode,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task PrepareShortcutTargetAsync(nint target,
            CancellationToken token)
        {
            long start = Stopwatch.GetTimestamp();
            int stable = 0;
            bool requested = false;
            int requestError = 0;
            ShortcutTargetState state;
            do
            {
                token.ThrowIfCancellationRequested();
                state = ReadShortcutTarget(target);
                if (!Native.IsWindow(target) || state.Owner == 0)
                {
                    break;
                }

                if (state.Foreground != state.Root)
                {
                    Marshal.SetLastPInvokeError(0);
                    requested = windowActivation.RequestForeground(state.Root);
                    requestError = Marshal.GetLastPInvokeError();
                    stable = 0;
                }
                else if (!state.Matches(target))
                {
                    SetShortcutFocus(target, state);
                    stable = 0;
                }
                else if (++stable == 2)
                {
                    // 接続解除後の別の観測でも一致してから送信を許可します。
                    return;
                }

                await DelayMillisecondsAsync(ShortcutFocusPollMilliseconds, token).ConfigureAwait(false);
            }
            while (Stopwatch.GetElapsedTime(start) < ShortcutFocusTimeout);

            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "SCKEYの送信対象が前面とフォーカスを保持できませんでした。"
                + $" requested={requested} requestError={requestError}"
                + " " + state.Describe(target)));
        }

        private void VerifyShortcutTarget(nint target)
        {
            ShortcutTargetState state = ReadShortcutTarget(target);
            if (!Native.IsWindow(target)
                || !state.Matches(target))
            {
                throw WindowsOperationErrors.Record(new InvalidOperationException(
                    "SCKEYの送信直前に対象の前面またはフォーカスが変わりました。"
                    + " " + state.Describe(target)));
            }
        }

        private ShortcutTargetState ReadShortcutTarget(nint target)
        {
            nint root = Native.GetAncestor(target, 2);
            if (root == 0)
            {
                root = target;
            }

            uint owner = Native.GetWindowThreadProcessId(target, out _);
            var info = new Native.GuiThreadInfo
            {
                Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>()
            };
            Marshal.SetLastPInvokeError(0);
            bool found = owner != 0 && Native.GetGUIThreadInfo(owner, ref info);
            int error = Marshal.GetLastPInvokeError();
            // 準備中と送信直前で、同じ前面の観測元を使います。
            return new(root, lookup.ForegroundWindow(), info.Active,
                info.Focus, owner, found, error);
        }

        private static void SetShortcutFocus(nint target,
            ShortcutTargetState state)
        {
            uint current = Native.GetCurrentThreadId();
            bool attached = state.Owner != current;
            if (attached && !Native.AttachThreadInput(current, state.Owner, true))
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            Exception? primary = null;
            try
            {
                ShortcutSetActiveWindow(state.Root);
                if (target != state.Root)
                {
                    ShortcutSetFocus(target);
                }
            }
            catch (Exception error)
            {
                primary = error;
                throw;
            }
            finally
            {
                if (attached && !Native.AttachThreadInput(current, state.Owner, false))
                {
                    Win32Exception cleanup = WindowsOperationErrors.Record(
                        new Win32Exception(Marshal.GetLastWin32Error()));
                    if (primary is null)
                    {
                        throw cleanup;
                    }

                    primary.Data[ShortcutKeySequence.CleanupErrorsKey] =
                        new AggregateException(cleanup);
                }
            }
        }

        private readonly record struct ShortcutTargetState(nint Root,
            nint Foreground, nint Active, nint Focus, uint Owner,
            bool Found, int Error)
        {
            internal bool Matches(nint target) =>
                Found && Foreground == Root && Active == Root
                && (target == Root || Focus == target);

            internal string Describe(nint target) =>
                $"target={target:X} root={Root:X} foreground={Foreground:X}"
                + $" active={Active:X} focus={Focus:X} owner={Owner}"
                + $" current={Native.GetCurrentThreadId()} info={Found} error={Error}";
        }
    }
}
