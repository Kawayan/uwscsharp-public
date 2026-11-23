using System.ComponentModel;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 実行全体の入力禁止状態を操作します。
/// </summary>
internal interface IInputOwnershipSession
{
    Task SetInputBlockedAsync(bool blocked);
}

public sealed partial class WindowsAutomation
{
    private sealed partial class Session : IInputOwnershipSession
    {
        private async ValueTask<Value> InvokeLockHardAsync(
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            var request = InputLockRequest.Read(arguments);
            token.ThrowIfCancellationRequested();
            try
            {
                await SetInputBlockedAsync(request.Blocked).ConfigureAwait(false);
                return Value.Empty;
            }
            catch (Win32Exception error)
            {
                throw ScriptError(BuiltinFunctionNames.LockHard, error);
            }
        }

        public Task SetInputBlockedAsync(bool blocked)
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return resources.SetInputBlockedAsync(blocked);
            }
        }
    }

    private sealed partial class RunResources
    {
        private InputOwnershipDispatcher? inputOwner;
        private Task? inputRelease;
        private bool inputStopping;
        private readonly List<CancellationTokenRegistration> inputRegistrations = [];

        internal Task SetInputBlockedAsync(bool blocked)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing || inputStopping, this);
                return GetInputOwner().SetBlockedAsync(blocked);
            }
        }

        private InputOwnershipDispatcher GetInputOwner() => inputOwner ??= new(
            inputOwnershipFactory?.Invoke() ?? throw new NotSupportedException(
                "入力所有バックエンドが接続されていません。"));

        internal bool TrySendOwnedInput(Native.Input input)
        {
            Task? sending = SendOwnedInputAsync(input);
            if (sending is null)
            {
                return false;
            }

            sending.GetAwaiter().GetResult();
            return true;
        }

        internal Task? SendOwnedInputAsync(Native.Input input)
        {
            lock (gate)
            {
                if (inputOwnershipFactory is null)
                {
                    return null;
                }

                ObjectDisposedException.ThrowIf(disposed, this);
                // 禁止の解除後も、子Sessionが保持するUPを送れるようにします。
                return GetInputOwner().SendAsync(input);
            }
        }

        internal void ConfigureInputCancellation(CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                if (inputOwnershipFactory is not null && token.CanBeCanceled)
                {
                    inputRegistrations.Add(token.UnsafeRegister(
                        static state => ((RunResources)state!).RequestInputRelease(), this));
                }
            }
        }

        private Task RequestInputRelease()
        {
            lock (gate)
            {
                inputStopping = true;
                return inputRelease ??= inputOwner?.SetBlockedAsync(false)
                    ?? Task.CompletedTask;
            }
        }

        internal async Task StopInputBlockingAsync(List<Exception> errors)
        {
            try
            {
                await RequestInputRelease().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        private async Task DisposeInputAsync(List<Exception> errors)
        {
            CancellationTokenRegistration[] registrations;
            InputOwnershipDispatcher? owned;
            lock (gate)
            {
                registrations = [.. inputRegistrations];
                owned = inputOwner;
            }

            foreach (CancellationTokenRegistration registration in registrations)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }

            if (owned is not null)
            {
                try
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }
    }
}
